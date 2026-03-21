using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Pico8Emu
{
    public class Cart
    {
        public string   LuaCode  { get; set; } = string.Empty;
        public byte[]   Gfx      { get; set; } = new byte[128 * 128]; // 1 byte/pixel, 4-bit colour index
        public byte[]   Map      { get; set; } = new byte[128 * 64];  // 1 byte/tile
        public byte[]   SprFlags { get; set; } = new byte[256];       // 1 byte/sprite
        public P8Sfx[]  Sfx      { get; set; } = MakeEmptySfx();
        public P8Music[]Music    { get; set; } = MakeEmptyMusic();

        private static P8Sfx[] MakeEmptySfx()
        {
            var a = new P8Sfx[64];
            for (int i = 0; i < 64; i++) a[i] = new P8Sfx();
            return a;
        }
        private static P8Music[] MakeEmptyMusic()
        {
            var a = new P8Music[64];
            for (int i = 0; i < 64; i++) a[i] = new P8Music();
            return a;
        }
    }

    public static class CartLoader
    {
        public static Cart Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"ROM not found: {path}");

            bool isPng = path.EndsWith(".png",     StringComparison.OrdinalIgnoreCase)
                      || path.EndsWith(".p8.png",  StringComparison.OrdinalIgnoreCase);

            return isPng ? LoadPng(path) : LoadP8(path);
        }

        // ════════════════════════════════════════════════════════════════════
        // ── .p8 plain-text ─────────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════

        private static Cart LoadP8(string path)
        {
            var cart = new Cart();
            var lines = File.ReadAllLines(path);
            var sec   = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string section = "";

            foreach (var line in lines)
            {
                if (line.StartsWith("__") && line.EndsWith("__") && line.Length > 4)
                {
                    section = line.Trim('_').ToLower();
                    if (!sec.ContainsKey(section)) sec[section] = new List<string>();
                }
                else if (section.Length > 0)
                    sec[section].Add(line);
            }

            if (sec.TryGetValue("lua", out var lua)) cart.LuaCode = string.Join("\n", lua);
            if (sec.TryGetValue("gfx", out var gfx)) ParseGfx(gfx, cart.Gfx);
            if (sec.TryGetValue("map", out var map)) ParseMap(map, cart.Map);
            if (sec.TryGetValue("gff", out var gff)) ParseGff(gff, cart.SprFlags);
            if (sec.TryGetValue("sfx", out var sfx)) ParseSfxSection(sfx, cart.Sfx);
            if (sec.TryGetValue("music", out var mus)) ParseMusicSection(mus, cart.Music);
            return cart;
        }

        // ════════════════════════════════════════════════════════════════════
        // ── .p8.png ─────────────────────────────────────────────────────────
        //
        // The PNG is 160×205 pixels = 32,800 pixels.
        // Each cart byte is encoded in the low 2 bits of each colour channel,
        // in ARGB order: byte = (A&3)<<6 | (R&3)<<4 | (G&3)<<2 | (B&3)
        //
        // Cart RAM layout (mirrors PICO-8 memory):
        //   0x0000–0x1FFF  Sprite sheet  (4 bpp packed)
        //   0x2000–0x2FFF  Map top half  (128×32 tiles)
        //   0x3000–0x30FF  Sprite flags
        //   0x3100–0x42FF  Music / SFX   (ignored)
        //   0x4300–0x7FFF  Lua code      (see below)
        //
        // Lua at 0x4300:
        //   \x00pxa  → new compressed format (v0.2.0+, move-to-front)
        //   :c:\x00  → old compressed format (pre-v0.2.0, LZ-like)
        //   other    → raw ASCII up to first null
        // ════════════════════════════════════════════════════════════════════

        private static Cart LoadPng(string path)
        {
            using var image = Image.Load<Rgba32>(path);
            Console.WriteLine($"[PNG] {Path.GetFileName(path)}  {image.Width}×{image.Height}");

            // ── Steganographic extraction ────────────────────────────────────
            int totalPixels = image.Width * image.Height;
            var data = new byte[totalPixels];
            int idx  = 0;

            image.ProcessPixelRows(acc =>
            {
                for (int y = 0; y < acc.Height; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        ref Rgba32 p = ref row[x];
                        // ARGB: A=bits6-7 (MSB), R=bits4-5, G=bits2-3, B=bits0-1 (LSB)
                        data[idx++] = (byte)(((p.A & 3) << 6) |
                                             ((p.R & 3) << 4) |
                                             ((p.G & 3) << 2) |
                                              (p.B & 3));
                    }
                }
            });

            Console.WriteLine($"[PNG] Extracted {idx} steganographic bytes");

            var cart = new Cart();

            // Sprite sheet  0x0000–0x1FFF  (4 bpp: low nibble = left pixel)
            for (int i = 0; i < 0x2000 && i < data.Length; i++)
            {
                int pi = i * 2;
                int gx = pi % 128, gy = pi / 128;
                if (gy < 128)
                {
                    cart.Gfx[gy * 128 + gx]     = (byte)(data[i] & 0xF);
                    if (gx + 1 < 128)
                        cart.Gfx[gy * 128 + gx + 1] = (byte)((data[i] >> 4) & 0xF);
                }
            }

            // Map top half  0x2000–0x2FFF
            for (int i = 0; i < 0x1000 && 0x2000 + i < data.Length; i++)
                cart.Map[i] = data[0x2000 + i];

            // Sprite flags  0x3000–0x30FF
            if (0x3000 + 256 <= data.Length)
                Array.Copy(data, 0x3000, cart.SprFlags, 0, 256);

            // Music patterns  0x3100–0x31FF  (64 × 4 bytes)
            if (0x3100 + 256 <= data.Length)
                ParseMusicBinary(data, 0x3100, cart.Music);

            // SFX data  0x3200–0x42FF  (64 × 68 bytes)
            if (0x3200 + 64 * 68 <= data.Length)
                ParseSfxBinary(data, 0x3200, cart.Sfx);

            // Lua  0x4300+
            string? lua = ExtractLua(data);
            if (lua != null && lua.Length > 0)
            {
                cart.LuaCode = lua;
                Console.WriteLine($"[PNG] Lua extracted: {lua.Length} chars");
            }
            else
            {
                Console.WriteLine("[PNG] No Lua in pixel data — trying PNG text-chunk fallback");
                string? src = TryReadPngTextChunks(path);
                if (src != null)
                {
                    var embedded = ParseP8Text(src);
                    if (embedded.LuaCode.Length > 0)
                    {
                        cart.LuaCode = embedded.LuaCode;
                        Console.WriteLine($"[PNG] Lua from text chunk: {cart.LuaCode.Length} chars");
                    }
                }

                if (cart.LuaCode.Length == 0)
                    Console.WriteLine("[PNG] WARNING: No Lua found — screen will be black.");
            }

            return cart;
        }

        // ────────────────────────────────────────────────────────────────────
        // Lua extraction dispatcher
        // ────────────────────────────────────────────────────────────────────
        private static string? ExtractLua(byte[] data)
        {
            const int Base = 0x4300;
            if (Base + 4 > data.Length)
            {
                Console.WriteLine($"[PNG] Only {data.Length} bytes, cannot reach Lua at 0x{Base:X4}");
                return null;
            }

            byte b0 = data[Base], b1 = data[Base+1], b2 = data[Base+2], b3 = data[Base+3];
            Console.WriteLine($"[PNG] Lua magic @ 0x4300: {b0:X2} {b1:X2} {b2:X2} {b3:X2}");

            // New format: \x00pxa
            if (b0 == 0x00 && b1 == 'p' && b2 == 'x' && b3 == 'a')
                return DecompressNewPxa(data, Base);

            // Old format: :c:\x00
            if (b0 == ':' && b1 == 'c' && b2 == ':' && b3 == 0x00)
                return DecompressOldFormat(data, Base);

            // Plaintext up to first null byte
            var sb = new StringBuilder();
            for (int i = Base; i < data.Length; i++)
            {
                byte b = data[i];
                if (b == 0) break;
                if (b == '\n' || b == '\r' || (b >= 0x20 && b < 0x80))
                    sb.Append((char)b);
            }
            string raw = sb.ToString().Trim();
            return raw.Length > 4 ? raw : null;
        }

        // ════════════════════════════════════════════════════════════════════
        // ── New pxa decompressor (v0.2.0+) ──────────────────────────────────
        //
        // Header (at baseOfs):
        //   [0..3]   \x00pxa  magic
        //   [4..5]   decompressed length, big-endian
        //   [6..7]   compressed length + 8,  big-endian
        //   [8..]    bitstream, LSB-first per byte
        //
        // Token types (header bit first):
        //   1  → MTF literal: unary-coded index → byte from move-to-front table
        //   0  → back-reference OR uncompressed block
        // ════════════════════════════════════════════════════════════════════

        private static string? DecompressNewPxa(byte[] data, int baseOfs)
        {
            if (baseOfs + 8 > data.Length) return null;

            int decompLen = (data[baseOfs + 4] << 8) | data[baseOfs + 5];
            int totalSize = (data[baseOfs + 6] << 8) | data[baseOfs + 7];
            int compEnd   = Math.Min(data.Length, baseOfs + totalSize);
            int bytePos   = baseOfs + 8;
            int bitPos    = 0;

            int ReadBit()
            {
                if (bytePos >= compEnd) return 0;
                int bit = (data[bytePos] >> bitPos) & 1;
                if (++bitPos == 8) { bitPos = 0; bytePos++; }
                return bit;
            }

            int ReadBits(int n)
            {
                int v = 0;
                for (int i = 0; i < n; i++) v |= ReadBit() << i;
                return v;
            }

            // Move-to-front array: initially identity mapping
            byte[] mtf = new byte[256];
            for (int i = 0; i < 256; i++) mtf[i] = (byte)i;

            var sb = new StringBuilder(decompLen);

            while (sb.Length < decompLen && bytePos < compEnd)
            {
                if (ReadBit() == 1)
                {
                    // ── MTF literal ────────────────────────────────────────
                    int unary = 0;
                    while (unary < 8 && ReadBit() == 1) unary++;
                    int unaryMask = (1 << unary) - 1;
                    int index     = ReadBits(4 + unary) + (unaryMask << 4);
                    if (index > 255) index = 255;

                    byte val = mtf[index];
                    sb.Append((char)val);

                    // Move val to front of MTF array
                    for (int j = index; j > 0; j--) mtf[j] = mtf[j - 1];
                    mtf[0] = val;
                }
                else
                {
                    // ── Back-reference (or uncompressed block) ─────────────
                    int offsetBits = ReadBit() == 1
                        ? (ReadBit() == 1 ? 5 : 10)
                        : 15;
                    int offset = ReadBits(offsetBits) + 1;

                    // Special: offsetBits==10, offset==1 → uncompressed block
                    if (offsetBits == 10 && offset == 1)
                    {
                        while (sb.Length < decompLen)
                        {
                            int b = ReadBits(8);
                            if (b == 0) break;
                            sb.Append((char)b);
                        }
                    }
                    else
                    {
                        // Read match length: start at 3, accumulate 3-bit parts until part < 7
                        int length = 3, part;
                        do { part = ReadBits(3); length += part; } while (part == 7);

                        int srcPos = sb.Length - offset;
                        for (int i = 0; i < length && sb.Length < decompLen; i++)
                        {
                            int src = srcPos + i;
                            sb.Append(src >= 0 ? sb[src] : '\0');
                        }
                    }
                }
            }

            return sb.ToString();
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Old :c: decompressor (pre-v0.2.0) ───────────────────────────────
        //
        // Header (at baseOfs):
        //   [0..3]  :c:\x00  magic
        //   [4..5]  decompressed length, big-endian
        //   [6..7]  always zero
        //   [8..]   compressed bytes:
        //     0x00          → next byte copied literally
        //     0x01–0x3B     → character from lookup table (1-based)
        //     0x3C–0xFF     → back-reference: offset/length from this+next byte
        // ════════════════════════════════════════════════════════════════════

        private static string? DecompressOldFormat(byte[] data, int baseOfs)
        {
            if (baseOfs + 8 > data.Length) return null;

            int decompLen = (data[baseOfs + 4] << 8) | data[baseOfs + 5];
            const string Table = "\n 0123456789abcdefghijklmnopqrstuvwxyz!#%(){}[]<>+=/*:;.,~_";

            var sb  = new StringBuilder(decompLen);
            int pos = baseOfs + 8;

            while (pos < data.Length && sb.Length < decompLen)
            {
                byte b = data[pos++];

                if (b == 0x00)
                {
                    if (pos < data.Length) sb.Append((char)data[pos++]);
                }
                else if (b <= 0x3B)
                {
                    int idx = b - 1;
                    if (idx < Table.Length) sb.Append(Table[idx]);
                }
                else
                {
                    if (pos >= data.Length) break;
                    byte next   = data[pos++];
                    int  offset = (b - 0x3C) * 16 + (next & 0xF);
                    int  length = (next >> 4) + 2;
                    if (offset == 0) continue;

                    int copyFrom = sb.Length - offset;
                    for (int i = 0; i < length && sb.Length < decompLen; i++)
                    {
                        int src = copyFrom + i;
                        sb.Append(src >= 0 && src < sb.Length ? sb[src] : ' ');
                    }
                }
            }

            return sb.ToString();
        }

        // ════════════════════════════════════════════════════════════════════
        // ── PNG text-chunk fallback ──────────────────────────────────────────
        // (Some community tools embed the full .p8 source in a tEXt/zTXt chunk)
        // ════════════════════════════════════════════════════════════════════

        private static string? TryReadPngTextChunks(string path)
        {
            try
            {
                byte[] raw = File.ReadAllBytes(path);
                if (raw.Length < 12 || raw[0] != 0x89 || raw[1] != 0x50 ||
                    raw[2] != 0x4E || raw[3] != 0x47) return null;

                int pos = 8;
                while (pos + 12 <= raw.Length)
                {
                    int    len  = (raw[pos] << 24) | (raw[pos+1] << 16) | (raw[pos+2] << 8) | raw[pos+3];
                    string type = Encoding.ASCII.GetString(raw, pos + 4, 4);
                    int    dOfs = pos + 8;
                    int    dEnd = dOfs + len;
                    pos = dEnd + 4;

                    if (len <= 0 || dEnd > raw.Length) continue;

                    if (type == "tEXt" || type == "iTXt")
                    {
                        int sep = Array.IndexOf(raw, (byte)0, dOfs, len);
                        if (sep < 0) sep = dEnd;
                        string kw  = Encoding.Latin1.GetString(raw, dOfs, sep - dOfs);
                        string val = sep + 1 < dEnd
                            ? Encoding.Latin1.GetString(raw, sep + 1, dEnd - sep - 1) : "";
                        string? hit = PickP8(kw, val);
                        if (hit != null) return hit;
                    }
                    else if (type == "zTXt")
                    {
                        int sep = Array.IndexOf(raw, (byte)0, dOfs, len);
                        if (sep < 0) sep = dEnd;
                        string kw      = Encoding.Latin1.GetString(raw, dOfs, sep - dOfs);
                        int    compOfs = sep + 2;
                        if (compOfs >= dEnd) continue;
                        try
                        {
                            using var ms  = new MemoryStream(raw, compOfs, dEnd - compOfs);
                            using var zs  = new ZLibStream(ms, CompressionMode.Decompress);
                            using var buf = new MemoryStream();
                            zs.CopyTo(buf);
                            string val   = Encoding.Latin1.GetString(buf.ToArray());
                            string? hit  = PickP8(kw, val);
                            if (hit != null) return hit;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return null;
        }

        private static string? PickP8(string kw, string val)
        {
            const string Magic = "pico-8 cartridge";
            string v = val.TrimStart();
            if (v.StartsWith(Magic, StringComparison.OrdinalIgnoreCase)) return v;
            string c = (kw + "\n" + val).TrimStart();
            if (c.StartsWith(Magic, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        // ════════════════════════════════════════════════════════════════════
        // ── .p8 text parser (used for plain files and text-chunk fallback) ───
        // ════════════════════════════════════════════════════════════════════

        private static Cart ParseP8Text(string text)
        {
            var cart    = new Cart();
            var sec     = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string section = "";

            foreach (var line in text.Split('\n'))
            {
                var t = line.TrimEnd('\r');
                if (t.StartsWith("__") && t.EndsWith("__") && t.Length > 4)
                {
                    section = t.Trim('_').ToLower();
                    if (!sec.ContainsKey(section)) sec[section] = new List<string>();
                }
                else if (section.Length > 0)
                    sec[section].Add(t);
            }

            if (sec.TryGetValue("lua", out var lua)) cart.LuaCode = string.Join("\n", lua);
            if (sec.TryGetValue("gfx", out var gfx)) ParseGfx(gfx, cart.Gfx);
            if (sec.TryGetValue("map", out var map)) ParseMap(map, cart.Map);
            if (sec.TryGetValue("gff", out var gff)) ParseGff(gff, cart.SprFlags);
            if (sec.TryGetValue("sfx", out var sfx)) ParseSfxSection(sfx, cart.Sfx);
            if (sec.TryGetValue("music", out var mus)) ParseMusicSection(mus, cart.Music);
            return cart;
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Section parsers ─────────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════

        private static void ParseGfx(List<string> lines, byte[] gfx)
        {
            int pixel = 0;
            foreach (var line in lines)
                foreach (char c in line)
                {
                    if (pixel >= gfx.Length) return;
                    gfx[pixel++] = HexNibble(c);
                }
        }

        private static void ParseMap(List<string> lines, byte[] map)
        {
            int tile = 0;
            foreach (var line in lines)
            {
                int i = 0;
                while (i + 1 < line.Length && tile < map.Length)
                {
                    map[tile++] = (byte)((HexNibble(line[i]) << 4) | HexNibble(line[i + 1]));
                    i += 2;
                }
            }
        }

        private static void ParseGff(List<string> lines, byte[] flags)
        {
            int idx = 0;
            foreach (var line in lines)
            {
                int i = 0;
                while (i + 1 < line.Length && idx < flags.Length)
                {
                    flags[idx++] = (byte)((HexNibble(line[i]) << 4) | HexNibble(line[i + 1]));
                    i += 2;
                }
            }
        }

        private static byte HexNibble(char c) => c switch
        {
            >= '0' and <= '9' => (byte)(c - '0'),
            >= 'a' and <= 'f' => (byte)(c - 'a' + 10),
            >= 'A' and <= 'F' => (byte)(c - 'A' + 10),
            _ => 0
        };

        private static int HexByte(string s, int pos)
            => (pos + 1 < s.Length) ? (HexNibble(s[pos]) << 4) | HexNibble(s[pos + 1]) : 0;

        // ════════════════════════════════════════════════════════════════════
        // ── SFX section parser (.p8 text format) ────────────────────────────
        //
        // Each line = 1 SFX entry (168 chars when stripped of spaces):
        //   chars  0-1  : editor flags  (01 = has data)
        //   chars  2-3  : speed         (hex byte)
        //   chars  4-5  : loop_start    (hex byte)
        //   chars  6-7  : loop_end      (hex byte)
        //   chars  8-167: 32 notes × 5 chars each:
        //       PP = pitch (2 hex), W = wave (1 hex), V = vol (1 hex), E = fx (1 hex)
        // ════════════════════════════════════════════════════════════════════

        private static void ParseSfxSection(List<string> lines, P8Sfx[] sfx)
        {
            int slot = 0;
            foreach (var rawLine in lines)
            {
                if (slot >= 64) break;
                // Strip whitespace to normalize different spacing styles
                string line = rawLine.Replace(" ", "").Replace("\t", "");
                if (line.Length < 8) { slot++; continue; }

                var s = sfx[slot];

                // Header
                bool hasData   = HexNibble(line[0]) != 0 || HexNibble(line[1]) != 0
                              || line.Length >= 9; // any non-empty note data
                s.Speed      = HexByte(line, 2);
                s.LoopStart  = HexByte(line, 4);
                s.LoopEnd    = HexByte(line, 6);

                // Notes
                int notePos = 8;
                bool anyNote = false;
                for (int n = 0; n < 32 && notePos + 4 < line.Length; n++, notePos += 5)
                {
                    int pitch = HexByte(line, notePos);
                    int wave  = HexNibble(line[notePos + 2]);
                    int vol   = HexNibble(line[notePos + 3]);
                    int fx    = HexNibble(line[notePos + 4]);
                    s.Notes[n] = new P8Note
                    {
                        Pitch = (byte)(pitch & 0x3F),
                        Wave  = (byte)(wave  & 0x7),
                        Vol   = (byte)(vol   & 0x7),
                        Fx    = (byte)(fx    & 0x7),
                    };
                    if (vol > 0) anyNote = true;
                }

                s.HasData = anyNote;   // speed=0 treated as 1 by synthesizer
                slot++;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Music section parser (.p8 text format) ──────────────────────────
        //
        // Each line = 1 music pattern. After stripping spaces:
        //   chars 0-1  : flags byte
        //                  bit 0 (0x01) = loop_begin
        //                  bit 1 (0x02) = loop_end
        //                  bit 2 (0x04) = stop
        //   chars 2-3  : channel 0 sfx  (>= 64 = silent, bit6 = disabled)
        //   chars 4-5  : channel 1 sfx
        //   chars 6-7  : channel 2 sfx
        //   chars 8-9  : channel 3 sfx  (optional)
        // ════════════════════════════════════════════════════════════════════

        private static void ParseMusicSection(List<string> lines, P8Music[] music)
        {
            int slot = 0;
            foreach (var rawLine in lines)
            {
                if (slot >= 64) break;
                string line = rawLine.Replace(" ", "").Replace("\t", "");
                if (line.Length < 8) { slot++; continue; }

                var m = music[slot];
                int flags   = HexByte(line, 0);
                m.LoopBegin = (flags & 1) != 0;   // bit 0
                m.LoopEnd   = (flags & 2) != 0;   // bit 1
                m.Stop      = (flags & 4) != 0;   // bit 2

                for (int c = 0; c < 4 && (c + 1) * 2 + 2 <= line.Length; c++)
                {
                    int si = HexByte(line, 2 + c * 2);
                    // bit 6 set → channel disabled/silent
                    m.SfxIdx[c] = (si & 0x40) != 0 ? -1 : (si & 0x3F);
                }

                slot++;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // ── SFX binary parser (.p8.png memory at 0x3200) ────────────────────
        //
        // 64 SFX × 68 bytes each:
        //   bytes  0-63 : 32 notes (2 bytes LE each)
        //     bits  0-5  : pitch (0-63)
        //     bits  6-8  : waveform (0-7)
        //     bits  9-11 : volume (0-7)
        //     bits 12-14 : effect (0-7)
        //   byte  64: editor flags (bit 7 = has data)
        //   byte  65: speed (ticks/note)
        //   byte  66: loop_start
        //   byte  67: loop_end
        // ════════════════════════════════════════════════════════════════════

        private static void ParseSfxBinary(byte[] data, int baseOfs, P8Sfx[] sfx)
        {
            for (int i = 0; i < 64; i++)
            {
                int ofs = baseOfs + i * 68;
                var s   = sfx[i];

                bool anyNote = false;
                for (int n = 0; n < 32; n++)
                {
                    int lo  = data[ofs + n * 2];
                    int hi  = data[ofs + n * 2 + 1];
                    int raw = lo | (hi << 8);

                    s.Notes[n] = new P8Note
                    {
                        Pitch = (byte)( raw        & 0x3F),
                        Wave  = (byte)((raw >>  6)  & 0x7),
                        Vol   = (byte)((raw >>  9)  & 0x7),
                        Fx    = (byte)((raw >> 12)  & 0x7),
                    };
                    if (s.Notes[n].Vol > 0) anyNote = true;
                }

                s.Speed     = data[ofs + 65];
                s.LoopStart = data[ofs + 66];
                s.LoopEnd   = data[ofs + 67];
                s.HasData   = anyNote;   // speed=0 treated as 1 by synthesizer
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Music binary parser (.p8.png memory at 0x3100) ──────────────────
        //
        // 64 patterns × 4 bytes each (one byte per channel):
        //   byte 0: bit7=loop_begin, bit6=ch0_disabled, bits5-0=ch0 sfx
        //   byte 1: bit7=loop_end,   bit6=ch1_disabled, bits5-0=ch1 sfx
        //   byte 2: bit7=stop,       bit6=ch2_disabled, bits5-0=ch2 sfx
        //   byte 3: bit7=unused,     bit6=ch3_disabled, bits5-0=ch3 sfx
        // ════════════════════════════════════════════════════════════════════

        private static void ParseMusicBinary(byte[] data, int baseOfs, P8Music[] music)
        {
            for (int i = 0; i < 64; i++)
            {
                int ofs = baseOfs + i * 4;
                var m   = music[i];

                for (int c = 0; c < 4; c++)
                {
                    byte b = data[ofs + c];
                    bool disabled = (b & 0x40) != 0;
                    m.SfxIdx[c]  = disabled ? -1 : (b & 0x3F);

                    if      (c == 0) m.LoopBegin = (b & 0x80) != 0;
                    else if (c == 1) m.LoopEnd   = (b & 0x80) != 0;
                    else if (c == 2) m.Stop       = (b & 0x80) != 0;
                }
            }
        }
    }
}

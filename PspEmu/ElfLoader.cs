using System.Buffers.Binary;
using System.Text;

namespace PspEmu;

/// <summary>
/// Loads PSP PRX modules and ELF executables.
/// Implements PSP-specific relocation (matching PPSSPP's ElfReader::LoadRelocations).
/// Parses SceModuleInfo and resolves import stubs by NID.
/// </summary>
sealed class ElfLoader
{
    public uint EntryPoint { get; private set; }
    public uint TextBase { get; private set; }
    public uint TextEnd { get; private set; }
    public uint GpValue { get; private set; }
    public bool IsPrx { get; private set; }
    public string ModuleName { get; private set; } = "";
    public IsoReader? Iso { get; private set; }

    readonly PspBus _bus;
    readonly HleModules _modules;

    // Segment virtual addresses (after loading), indexed by segment number
    readonly uint[] _segmentVAddr = new uint[16];

    public ElfLoader(PspBus bus, HleModules modules)
    {
        _bus = bus;
        _modules = modules;
    }

    // ── Public API ──

    public bool LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            Log.Error(LogCat.Loader, $"File not found: {path}");
            return false;
        }

        string ext = Path.GetExtension(path).ToLowerInvariant();

        // ISO/CSO disc image: extract PSP_GAME/SYSDIR/EBOOT.BIN
        if (ext is ".iso" or ".cso")
            return LoadFromIso(path);

        // If user passes a .elf, try .prx or EBOOT.PBP first (those have proper PSP relocations)
        if (ext == ".elf")
        {
            string dir = Path.GetDirectoryName(path) ?? ".";
            string baseName = Path.GetFileNameWithoutExtension(path);
            string prxPath = Path.Combine(dir, baseName + ".prx");
            string pbpPath = Path.Combine(dir, "EBOOT.PBP");

            if (File.Exists(prxPath))
            {
                Log.Write(LogCat.Loader, $"Found PRX alongside ELF, loading {prxPath} instead");
                return Load(File.ReadAllBytes(prxPath));
            }
            if (File.Exists(pbpPath))
            {
                Log.Write(LogCat.Loader, $"Found EBOOT.PBP alongside ELF, loading {pbpPath} instead");
                return Load(File.ReadAllBytes(pbpPath));
            }
        }

        Log.Write(LogCat.Loader, $"Loading {path}");
        return Load(File.ReadAllBytes(path));
    }

    bool LoadFromIso(string isoPath)
    {
        Iso = IsoReader.Open(isoPath);
        if (Iso == null)
        {
            Log.Error(LogCat.Loader, $"Failed to open ISO: {isoPath}");
            return false;
        }

        // PSP UMD layout: PSP_GAME/SYSDIR/EBOOT.BIN (the main executable PBP/PRX)
        string[] candidates = {
            "PSP_GAME/SYSDIR/EBOOT.BIN",
            "PSP_GAME/SYSDIR/BOOT.BIN",
            "PSP_GAME/SYSDIR/EBOOT.PBP",
        };

        foreach (var candidate in candidates)
        {
            byte[]? data = Iso.ReadFile(candidate);
            if (data != null && data.Length > 0)
            {
                Log.Write(LogCat.Loader, $"ISO: loading {candidate} ({data.Length} bytes)");
                return Load(data);
            }
        }

        Log.Error(LogCat.Loader, "ISO: no EBOOT.BIN found in PSP_GAME/SYSDIR/");
        return false;
    }

    public bool LoadFromDirectory(string dir)
    {
        string[] candidates = { "EBOOT.PBP", "EBOOT.BIN", "EBOOT.ELF" };
        foreach (var name in candidates)
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path))
                return LoadFile(path);
        }
        foreach (var f in Directory.GetFiles(dir, "*.prx"))
            return LoadFile(f);
        foreach (var f in Directory.GetFiles(dir, "*.elf"))
            return LoadFile(f);

        Log.Error(LogCat.Loader, $"No loadable file found in {dir}");
        return false;
    }

    // ── Loading ──

    bool Load(byte[] data)
    {
        if (data.Length < 52) return false;

        // PBP container
        if (data[0] == 0x00 && data[1] == 0x50 && data[2] == 0x42 && data[3] == 0x50)
            return LoadPbp(data);

        // Encrypted PRX
        if (data[0] == 0x7E && data[1] == 0x50 && data[2] == 0x53 && data[3] == 0x50)
        {
            Log.Error(LogCat.Loader, "Encrypted PRX not supported");
            return false;
        }

        // Standard ELF magic
        if (data[0] != 0x7F || data[1] != (byte)'E' || data[2] != (byte)'L' || data[3] != (byte)'F')
        {
            Log.Error(LogCat.Loader, "Not a valid ELF file");
            return false;
        }

        return LoadElf(data);
    }

    bool LoadPbp(byte[] data)
    {
        if (data.Length < 0x28) return false;
        // PBP header: magic(4) + version(4) + offsets[8]*4
        // offsets[6] = DATA.PSP (the PRX executable) at byte 0x20
        // offsets[7] = DATA.PSAR at byte 0x24
        uint dataPspOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x20));
        uint dataPsarOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x24));
        if (dataPspOffset == 0 || dataPspOffset >= (uint)data.Length) return false;

        int end = (dataPsarOffset > dataPspOffset && dataPsarOffset <= (uint)data.Length)
            ? (int)dataPsarOffset
            : data.Length;
        byte[] inner = data.AsSpan((int)dataPspOffset, end - (int)dataPspOffset).ToArray();
        Log.Write(LogCat.Loader, $"PBP: extracted DATA.PSP at offset 0x{dataPspOffset:X}, size={inner.Length}");
        return Load(inner);
    }

    bool LoadElf(byte[] data)
    {
        ushort elfType = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(16));
        // PRX type: 0xFFA0 or any 0xFFxx
        IsPrx = elfType != 2; // anything other than ET_EXEC is relocatable
        bool bRelocate = IsPrx;

        uint entry = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(24));
        uint phOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(28));
        uint shOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(32));
        ushort phEntSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(42));
        ushort phNum = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(44));
        ushort shEntSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(46));
        ushort shNum = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(48));
        ushort shStrIdx = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(50));

        // Find total address range
        uint totalStart = 0xFFFFFFFF, totalEnd = 0;
        for (int i = 0; i < phNum; i++)
        {
            int off = (int)(phOff + i * phEntSize);
            if (off + phEntSize > data.Length) break;
            uint pType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off));
            uint pVaddr = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 8));
            uint pMemSz = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 20));
            if (pType != 1) continue; // PT_LOAD
            if (pVaddr < totalStart) totalStart = pVaddr;
            if (pVaddr + pMemSz > totalEnd) totalEnd = pVaddr + pMemSz;
        }

        // Determine load address (matching PPSSPP logic)
        uint vaddr;
        if (!bRelocate)
        {
            // Pre-relocated: load at the ELF's own virtual address
            vaddr = totalStart;
        }
        else
        {
            // Relocatable: load in user memory
            vaddr = 0x0880_0000;
        }

        uint baseAddress = bRelocate ? vaddr : 0;

        // Load segments
        for (int i = 0; i < phNum; i++)
        {
            int off = (int)(phOff + i * phEntSize);
            if (off + phEntSize > data.Length) break;

            uint pType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off));
            uint pOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 4));
            uint pVaddr = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 8));
            uint pPaddr = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 12));
            uint pFileSz = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 16));
            uint pMemSz = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 20));

            if (pType != 1) continue; // PT_LOAD

            uint writeAddr = baseAddress + pVaddr;
            _segmentVAddr[i] = writeAddr;

            if (pFileSz > 0 && pOffset + pFileSz <= data.Length)
            {
                uint physDest = PspBus.VirtToPhys(writeAddr);
                if (physDest < PspBus.RamSize && physDest + pFileSz <= PspBus.RamSize)
                    Array.Copy(data, (int)pOffset, _bus.Ram, (int)physDest, (int)pFileSz);
                else
                    _bus.WriteBlock(writeAddr, data.AsSpan((int)pOffset, (int)pFileSz));
            }

            // Zero BSS
            if (pMemSz > pFileSz)
            {
                uint bssStart = writeAddr + pFileSz;
                uint bssLen = pMemSz - pFileSz;
                uint physBss = PspBus.VirtToPhys(bssStart);
                if (physBss < PspBus.RamSize && physBss + bssLen <= PspBus.RamSize)
                    Array.Clear(_bus.Ram, (int)physBss, (int)Math.Min(bssLen, PspBus.RamSize - physBss));
            }
        }

        TextBase = baseAddress + totalStart;
        TextEnd = baseAddress + totalEnd;
        EntryPoint = bRelocate ? entry + vaddr : entry;

        // Apply PSP-specific relocations (SHT_PSPREL = 0x700000A0)
        if (bRelocate)
            ApplyPspRelocations(data, shOff, shEntSize, shNum, shStrIdx);

        // Find SceModuleInfo
        FindAndParseModuleInfo(data, phOff, phEntSize, phNum, shOff, shEntSize, shNum, shStrIdx, baseAddress);

        Log.Write(LogCat.Loader, $"Loaded: '{ModuleName}' entry={EntryPoint:X8} base={TextBase:X8} end={TextEnd:X8} gp={GpValue:X8} prx={IsPrx}");
        return true;
    }

    // ── PSP-Specific Relocations (matching PPSSPP) ──

    void ApplyPspRelocations(byte[] data, uint shOff, ushort shEntSize, ushort shNum, ushort shStrIdx)
    {
        int totalRelocs = 0;

        for (int i = 0; i < shNum; i++)
        {
            int off = (int)(shOff + i * shEntSize);
            if (off + shEntSize > data.Length) break;

            uint shType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 4));
            if (shType != 0x700000A0) continue; // SHT_PSPREL

            // Only relocate sections with SHF_ALLOC
            uint shInfo = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 28));
            if (shInfo > 0 && shInfo < shNum)
            {
                int targetSecOff = (int)(shOff + shInfo * shEntSize);
                uint targetFlags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(targetSecOff + 8));
                if ((targetFlags & 0x2) == 0) continue; // SHF_ALLOC = 0x2
            }

            uint relOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 16));
            uint relSz = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 20));
            int numRelocs = (int)(relSz / 8);

            totalRelocs += LoadRelocations(data, relOff, numRelocs);
        }

        Log.Write(LogCat.Loader, $"Relocations applied: {totalRelocs}");
    }

    /// <summary>
    /// PSP relocation format (from PPSSPP ElfReader::LoadRelocations):
    /// r_info: type = info & 0xF, readwrite = (info >> 8) & 0xFF, relative = (info >> 16) & 0xFF
    /// addr = r_offset + segmentVAddr[readwrite]
    /// relocateTo = segmentVAddr[relative]
    /// </summary>
    int LoadRelocations(byte[] data, uint relFileOff, int numRelocs)
    {
        // Phase 1: Read all ops from memory (before any modifications)
        uint[] ops = new uint[numRelocs];
        uint[] addrs = new uint[numRelocs];
        uint[] infos = new uint[numRelocs];

        for (int r = 0; r < numRelocs; r++)
        {
            int rOff = (int)(relFileOff + r * 8);
            if (rOff + 8 > data.Length) break;

            uint rOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(rOff));
            uint rInfo = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(rOff + 4));

            int type = (int)(rInfo & 0xF);
            int readwrite = (int)((rInfo >> 8) & 0xFF);

            if (readwrite >= _segmentVAddr.Length || _segmentVAddr[readwrite] == 0)
                continue;

            uint addr = rOffset + _segmentVAddr[readwrite];
            addrs[r] = addr;
            infos[r] = rInfo;
            ops[r] = _bus.Read32(addr);
        }

        // Phase 2: Apply relocations
        int count = 0;
        for (int r = 0; r < numRelocs; r++)
        {
            uint rInfo = infos[r];
            int type = (int)(rInfo & 0xF);
            int relative = (int)((rInfo >> 16) & 0xFF);

            uint addr = addrs[r];
            if (addr == 0) continue;

            uint op = ops[r];
            uint relocateTo = (relative < _segmentVAddr.Length) ? _segmentVAddr[relative] : 0;

            switch (type)
            {
                case 0: // R_MIPS_NONE
                    break;

                case 2: // R_MIPS_32
                    op += relocateTo;
                    _bus.Write32(addr, op);
                    count++;
                    break;

                case 4: // R_MIPS_26 (j/jal)
                    op = (op & 0xFC000000) | (((op & 0x03FFFFFF) + (relocateTo >> 2)) & 0x03FFFFFF);
                    _bus.Write32(addr, op);
                    count++;
                    break;

                case 5: // R_MIPS_HI16
                {
                    uint cur = (op & 0xFFFFu) << 16;
                    bool found = false;
                    for (int t = r + 1; t < numRelocs; t++)
                    {
                        int tType = (int)(infos[t] & 0xF);
                        if (tType == 5) continue; // skip consecutive HI16
                        if (tType != 6 && tType != 1) break; // expect LO16

                        short lo = (short)(ops[t] & 0xFFFF);
                        cur = (uint)((int)cur + lo + (int)relocateTo);
                        ushort hi;
                        AddrToHiLo(cur, out hi, out lo);
                        op = (op & 0xFFFF0000) | hi;
                        found = true;
                        break;
                    }
                    if (!found)
                        Log.Warn(LogCat.Loader, $"R_MIPS_HI16 at {addr:X8}: no matching LO16");
                    _bus.Write32(addr, op);
                    count++;
                    break;
                }

                case 1: // R_MIPS_16 (fallthrough)
                case 6: // R_MIPS_LO16
                {
                    uint cur = op & 0xFFFF;
                    cur += relocateTo;
                    op = (op & 0xFFFF0000) | (cur & 0xFFFF);
                    _bus.Write32(addr, op);
                    count++;
                    break;
                }

                case 7: // R_MIPS_GPREL16
                    break; // safe to ignore
            }
        }
        return count;
    }

    static void AddrToHiLo(uint addr, out ushort hi, out short lo)
    {
        lo = (short)(addr & 0xFFFF);
        uint naddr = addr - (uint)(int)lo;
        hi = (ushort)(naddr >> 16);
    }

    // ── Module Info & Import Resolution ──

    void FindAndParseModuleInfo(byte[] data, uint phOff, ushort phEntSize, ushort phNum,
        uint shOff, ushort shEntSize, ushort shNum, ushort shStrIdx, uint baseAddress)
    {
        // Method 1: Look for .rodata.sceModuleInfo section
        if (shNum > 0 && shStrIdx < shNum)
        {
            int strTabOff = (int)(shOff + shStrIdx * shEntSize);
            if (strTabOff + shEntSize <= data.Length)
            {
                uint strTabFileOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(strTabOff + 16));
                for (int i = 0; i < shNum; i++)
                {
                    int off = (int)(shOff + i * shEntSize);
                    if (off + shEntSize > data.Length) break;
                    uint nameOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off));
                    uint shAddr = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 12));
                    uint namePos = strTabFileOff + nameOff;
                    if (namePos < data.Length)
                    {
                        string secName = ReadCString(data, (int)namePos);
                        if (secName == ".rodata.sceModuleInfo")
                        {
                            // After relocation, section addr is already correct
                            uint miAddr = baseAddress + shAddr;
                            ParseModuleInfo(miAddr);
                            return;
                        }
                    }
                }
            }
        }

        // Method 2: First segment p_paddr (masked, matching PPSSPP)
        if (phNum > 0)
        {
            int off = (int)phOff;
            uint pPaddr = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 12));
            uint miOffset = pPaddr & 0x7FFFFFFF;
            if (miOffset > 0 && miOffset + 0x34 <= data.Length)
            {
                uint miAddr = baseAddress + miOffset;
                ParseModuleInfo(miAddr);
                return;
            }
        }

        Log.Warn(LogCat.Loader, "SceModuleInfo not found");
    }

    void ParseModuleInfo(uint addr)
    {
        // PspModuleInfo layout (from PPSSPP):
        // +0x00: u16 moduleAttrs
        // +0x02: u16 moduleVersion
        // +0x04: char[28] name
        // +0x20: u32 gp
        // +0x24: u32 libent      (export table start)
        // +0x28: u32 libentend   (export table end)
        // +0x2C: u32 libstub     (import stub table start)
        // +0x30: u32 libstubend  (import stub table end)

        var sb = new StringBuilder();
        for (int i = 0; i < 28; i++)
        {
            byte b = _bus.Read8(addr + 4 + (uint)i);
            if (b == 0) break;
            sb.Append((char)b);
        }
        ModuleName = sb.ToString();
        GpValue = _bus.Read32(addr + 0x20);

        uint libstub = _bus.Read32(addr + 0x2C);
        uint libstubend = _bus.Read32(addr + 0x30);

        Log.Write(LogCat.Loader, $"ModuleInfo: '{ModuleName}' gp={GpValue:X8} stubs={libstub:X8}-{libstubend:X8}");

        if (libstub != 0 && libstubend > libstub)
            ResolveImports(libstub, libstubend);
    }

    /// <summary>
    /// Walk import stub table and resolve NIDs, matching PPSSPP's KernelImportModuleFuncs.
    /// PspLibStubEntry: name(u32), version(u16), flags(u16), size(u8 words), numVars(u8),
    ///                  numFuncs(u16), nidData(u32), firstSymAddr(u32)
    /// Each function stub is 8 bytes (2 instructions) starting at firstSymAddr.
    /// </summary>
    void ResolveImports(uint libstub, uint libstubend)
    {
        int totalResolved = 0, totalUnresolved = 0;

        // Walk as array of u32 words (matching PPSSPP's entryPos pointer)
        uint pos = libstub;
        while (pos < libstubend)
        {
            uint namePtr = _bus.Read32(pos + 0);
            byte entrySize = _bus.Read8(pos + 8);  // size in 32-bit words
            byte numVars = _bus.Read8(pos + 9);
            ushort numFuncs = _bus.Read16(pos + 10);
            uint nidData = _bus.Read32(pos + 12);
            uint firstSymAddr = _bus.Read32(pos + 16);

            uint entrySizeBytes = (uint)entrySize * 4;
            if (entrySizeBytes == 0)
            {
                Log.Warn(LogCat.Loader, "Invalid import entry size 0, skipping");
                pos += 4;
                continue;
            }

            string moduleName = ReadStringFromMem(namePtr);
            Log.Write(LogCat.Loader, $"Import '{moduleName}': {numFuncs} funcs, nids={nidData:X8} stubs={firstSymAddr:X8}");

            if (numFuncs > 0 && nidData != 0)
            {
                for (int i = 0; i < numFuncs; i++)
                {
                    uint nid = _bus.Read32(nidData + (uint)(i * 4));
                    uint stubAddr = firstSymAddr + (uint)(i * 8);

                    uint syscallCode = _modules.GetSyscallForNid(nid);
                    if (syscallCode != 0xFFFFFFFF)
                    {
                        // JR $ra (delay slot executes syscall first)
                        _bus.Write32(stubAddr, 0x03E00008);     // jr $ra
                        _bus.Write32(stubAddr + 4, 0x0000000C | (syscallCode << 6)); // syscall
                        totalResolved++;
                    }
                    else
                    {
                        // Unresolved: return 0
                        _bus.Write32(stubAddr, 0x03E00008);     // jr $ra
                        _bus.Write32(stubAddr + 4, 0x00001021); // move v0, zero
                        totalUnresolved++;
                        Log.Warn(LogCat.Loader, $"  Unresolved NID 0x{nid:X8} in {moduleName}");
                    }
                }
            }

            pos += entrySizeBytes;
        }

        Log.Write(LogCat.Loader, $"Imports: {totalResolved} resolved, {totalUnresolved} unresolved");
    }

    // ── Helpers ──

    string ReadStringFromMem(uint addr)
    {
        if (addr == 0) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < 128; i++)
        {
            byte b = _bus.Read8(addr + (uint)i);
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    static string ReadCString(byte[] data, int offset)
    {
        var sb = new StringBuilder();
        for (int i = offset; i < data.Length && i < offset + 128; i++)
        {
            if (data[i] == 0) break;
            sb.Append((char)data[i]);
        }
        return sb.ToString();
    }
}

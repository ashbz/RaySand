using System;
using System.Collections.Generic;
using MoonSharp.Interpreter;

namespace Pico8Emu
{
    /// <summary>
    /// PICO-8 fantasy console: 128×128 screen, 16-color palette, Lua VM.
    /// </summary>
    public class Pico8Machine
    {
        // ── Constants ────────────────────────────────────────────────────────
        public const int ScreenWidth  = 128;
        public const int ScreenHeight = 128;

        // ── PICO-8 16-color hardware palette (RGB) ───────────────────────────
        public static readonly (byte R, byte G, byte B)[] DefaultPalette = new (byte, byte, byte)[]
        {
            (0x00, 0x00, 0x00), //  0 black
            (0x1D, 0x2B, 0x53), //  1 dark blue
            (0x7E, 0x25, 0x53), //  2 dark purple
            (0x00, 0x87, 0x51), //  3 dark green
            (0xAB, 0x52, 0x36), //  4 brown
            (0x5F, 0x57, 0x4F), //  5 dark grey
            (0xC2, 0xC3, 0xC7), //  6 light grey
            (0xFF, 0xF1, 0xE8), //  7 white
            (0xFF, 0x00, 0x4D), //  8 red
            (0xFF, 0xA3, 0x00), //  9 orange
            (0xFF, 0xEC, 0x27), // 10 yellow
            (0x00, 0xE4, 0x36), // 11 green
            (0x29, 0xAD, 0xFF), // 12 blue
            (0x83, 0x76, 0x9C), // 13 lavender
            (0xFF, 0x77, 0xA8), // 14 pink
            (0xFF, 0xCC, 0xAA), // 15 peach
        };

        // Mutable live palette — renderer and editors read/write this.
        public static (byte R, byte G, byte B)[] Palette = new (byte, byte, byte)[]
        {
            (0x00, 0x00, 0x00),
            (0x1D, 0x2B, 0x53),
            (0x7E, 0x25, 0x53),
            (0x00, 0x87, 0x51),
            (0xAB, 0x52, 0x36),
            (0x5F, 0x57, 0x4F),
            (0xC2, 0xC3, 0xC7),
            (0xFF, 0xF1, 0xE8),
            (0xFF, 0x00, 0x4D),
            (0xFF, 0xA3, 0x00),
            (0xFF, 0xEC, 0x27),
            (0x00, 0xE4, 0x36),
            (0x29, 0xAD, 0xFF),
            (0x83, 0x76, 0x9C),
            (0xFF, 0x77, 0xA8),
            (0xFF, 0xCC, 0xAA),
        };

        // ── Screen / data buffers ────────────────────────────────────────────
        // One byte per pixel – stores the draw-palette-mapped color index.
        public byte[] Screen      { get; } = new byte[ScreenWidth * ScreenHeight];
        // Display palette: applied at display time (pal mode 1). Exposed so renderer can use it.
        public byte[] DisplayPalette { get; } = new byte[16] { 0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15 };

        private byte[] _spriteSheet = new byte[128 * 128]; // 1 byte per pixel
        private byte[] _mapData     = new byte[128 * 64];  // 1 byte per tile
        private byte[] _spriteFlags = new byte[256];       // 8 flags per sprite
        // General-purpose RAM for peek/poke (0x4300-0x5DFF range)
        private byte[] _gpRam       = new byte[0x1B00];

        // ── Draw palette & transparency ──────────────────────────────────────
        private byte[] _drawPal  = new byte[16] { 0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15 };
        // Transparency per color index – applies only to sprite/map drawing. Default: color 0.
        private bool[] _transPal = new bool[16] { true,false,false,false,false,false,false,false,
                                                  false,false,false,false,false,false,false,false };

        // ── Draw state ───────────────────────────────────────────────────────
        private byte   _drawColor = 7;
        private int    _camX, _camY;
        private int    _clipX, _clipY, _clipW = ScreenWidth, _clipH = ScreenHeight;
        private int    _cursorX, _cursorY;

        // Fill pattern: 16-bit mask (bit 15 = top-left of 4×4 tile).
        private ushort _fillPattern           = 0;
        private bool   _fillpActive           = false;  // false = solid fill
        private byte   _fillpSecondaryColor   = 0;
        private bool   _fillpSecondaryOpaque  = false;  // true = secondary color, false = transparent

        // Persistent line endpoint (NaN = not set).
        private double _lineX = double.NaN, _lineY = double.NaN;

        // ── Lua VM ───────────────────────────────────────────────────────────
        private Script    _lua = null!;
        private DynValue? _fnInit, _fnUpdate, _fnUpdate60, _fnDraw;
        private bool      _use60fps; // true if cart has _update60

        // ── Audio ─────────────────────────────────────────────────────────────
        public Pico8Audio? Audio { get; private set; }

        // ── Last loaded cart (for hot-reload) ────────────────────────────────
        private Cart? _currentCart;

        // ── Final preprocessed Lua (after all rewrites) ───────────────────────
        public string PreprocessedLua { get; private set; } = string.Empty;

        // ── Live access to sprite sheet and map (editing modifies game rendering) ─
        public byte[] SpriteSheet => _spriteSheet;
        public byte[] MapData     => _mapData;

        // ── Console output capture ────────────────────────────────────────────
        public List<string> ConsoleLines { get; } = new List<string>();
        public void ClearConsole() => ConsoleLines.Clear();
        private void ConsoleLog(string msg)
        {
            ConsoleLines.Add(msg);
            if (ConsoleLines.Count > 500) ConsoleLines.RemoveAt(0);
        }

        // ── Arbitrary Lua execution (for REPL) ───────────────────────────────
        public string ExecLua(string code)
        {
            if (_lua == null) return "[no VM]";
            try
            {
                var result = _lua.DoString(code);
                string r = result.IsNil() ? "" : (result.ToString() ?? "");
                if (!string.IsNullOrEmpty(r)) ConsoleLog(r);
                return r;
            }
            catch (Exception ex)
            {
                string err = $"[err] {ex.Message}";
                ConsoleLog(err);
                return err;
            }
        }

        // ── Timing ───────────────────────────────────────────────────────────
        private Random _rng  = new Random();
        public  int    Tick  { get; private set; }

        // ────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Replace the Lua source at runtime, keeping all other cart data.
        /// The game is fully restarted with the new code.
        /// </summary>
        public void ReloadLua(string newCode)
        {
            if (_currentCart == null) return;
            var patched = new Cart
            {
                LuaCode  = newCode,
                Gfx      = _currentCart.Gfx,
                Map      = _currentCart.Map,
                SprFlags = _currentCart.SprFlags,
                Sfx      = _currentCart.Sfx,
                Music    = _currentCart.Music,
            };
            LoadCart(patched);
        }

        public void LoadCart(Cart cart)
        {
            _currentCart = cart;
            Array.Copy(cart.Gfx,      _spriteSheet, Math.Min(cart.Gfx.Length,      _spriteSheet.Length));
            Array.Copy(cart.Map,      _mapData,     Math.Min(cart.Map.Length,       _mapData.Length));
            Array.Copy(cart.SprFlags, _spriteFlags, Math.Min(cart.SprFlags.Length,  _spriteFlags.Length));

            Audio = new Pico8Audio(cart.Sfx, cart.Music);

            _lua = BuildLuaVm();
            string _rawLua  = cart.LuaCode;
            string _prepLua = PreprocessLua(_rawLua);
            try { System.IO.File.WriteAllText("debug_raw.lua",  _rawLua);  } catch { }
            _prepLua = RewriteForInAll(_prepLua);
            _prepLua = RewriteGotoScoping(_prepLua);
            PreprocessedLua = _prepLua;
            try { System.IO.File.WriteAllText("debug_pre.lua",  _prepLua); } catch { }
            try
            {
                _lua.DoString(_prepLua);
            }
            catch (MoonSharp.Interpreter.ScriptRuntimeException ex)
            {
                Console.WriteLine($"[LUA] Runtime error during load: {ex.DecoratedMessage}");
            }
            catch (MoonSharp.Interpreter.SyntaxErrorException ex)
            {
                Console.WriteLine($"[LUA] Syntax error: {ex.DecoratedMessage}");
            }

            _fnInit     = TryGetFn("_init");
            _fnUpdate   = TryGetFn("_update");
            _fnUpdate60 = TryGetFn("_update60");
            _fnDraw     = TryGetFn("_draw");
            _use60fps   = _fnUpdate60 != null;

            if (_fnInit != null) SafeCall(_fnInit, "_init");
        }

        public void Update(bool[] buttons)
        {
            Array.Copy(_btnCurrent, _btnPrevious, _btnCurrent.Length);
            Array.Copy(buttons, _btnCurrent, Math.Min(buttons.Length, _btnCurrent.Length));

            bool callUpdate = _use60fps
                ? _fnUpdate60 != null
                : _fnUpdate != null && (Tick & 1) == 0; // 30fps: every other tick

            if (callUpdate)
                SafeCall(_use60fps ? _fnUpdate60! : _fnUpdate!, "_update");

            Tick++;
        }

        public void Draw()
        {
            bool callDraw = _use60fps || (Tick & 1) == 0;
            if (callDraw && _fnDraw != null)
                SafeCall(_fnDraw, "_draw");
        }

        private void SafeCall(DynValue fn, string name)
        {
            try { _lua.Call(fn); }
            catch (MoonSharp.Interpreter.ScriptRuntimeException ex)
            {
                Console.WriteLine($"[lua {name}] {ex.DecoratedMessage}");
                if (ex.InnerException != null)
                    Console.WriteLine($"[lua {name}] (caused by) {ex.InnerException}");
            }
            catch (Exception ex) { Console.WriteLine($"[lua {name}] {ex}"); }
        }

        // ── Input ────────────────────────────────────────────────────────────
        private bool[] _btnCurrent  = new bool[8];
        private bool[] _btnPrevious = new bool[8];

        // ── Lua VM construction ───────────────────────────────────────────────
        private Script BuildLuaVm()
        {
            var s = new Script(CoreModules.Preset_SoftSandbox | CoreModules.Debug);

            // ── PICO-8 string-to-number coercion ─────────────────────────────
            // PICO-8 allows arithmetic on strings (e.g. "5" + 3 == 8).
            // Install metamethods on the string type so MoonSharp coerces strings.
            s.DoString(@"
local _tonum = function(v)
  if type(v) == 'number' then return v end
  if type(v) == 'string' then
    local n = tonumber(v)
    if n then return n end
    -- try hex
    n = tonumber(v, 16)
    if n then return n end
  end
  return 0
end
local _smt = {
  __add = function(a,b) return _tonum(a) + _tonum(b) end,
  __sub = function(a,b) return _tonum(a) - _tonum(b) end,
  __mul = function(a,b) return _tonum(a) * _tonum(b) end,
  __div = function(a,b) local d=_tonum(b) if d==0 then return 0 end return _tonum(a)/d end,
  __mod = function(a,b) local d=_tonum(b) if d==0 then return 0 end return _tonum(a)%d end,
  __unm = function(a) return -_tonum(a) end,
  __pow = function(a,b) return _tonum(a)^_tonum(b) end,
  __eq  = function(a,b) return _tonum(a)==_tonum(b) end,
  __lt  = function(a,b) return _tonum(a)< _tonum(b) end,
  __le  = function(a,b) return _tonum(a)<=_tonum(b) end,
}
debug.setmetatable('', _smt)
");

            // ── Math ─────────────────────────────────────────────────────────
            // Nil-safe helpers: treat nil as 0 for unary, or as identity for binary
            static double N(DynValue v) {
                if (v == null || v.IsNil()) return 0;
                if (v.Type == DataType.String) {
                    string sv = v.String ?? "";
                    if (sv.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && sv.Length > 2)
                        if (long.TryParse(sv.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out long hv)) return hv;
                    if (double.TryParse(sv, System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out double dv)) return dv;
                    return 0;
                }
                return v.CastToNumber() ?? 0;
            }
            s.Globals["abs"]  = DynValue.NewCallback((ctx, args) => DynValue.NewNumber(Math.Abs(N(args.Count > 0 ? args[0] : DynValue.Nil))));
            s.Globals["flr"]  = DynValue.NewCallback((ctx, args) => DynValue.NewNumber(Math.Floor(N(args.Count > 0 ? args[0] : DynValue.Nil))));
            s.Globals["ceil"] = DynValue.NewCallback((ctx, args) => DynValue.NewNumber(Math.Ceiling(N(args.Count > 0 ? args[0] : DynValue.Nil))));
            s.Globals["sqrt"] = DynValue.NewCallback((ctx, args) => DynValue.NewNumber(Math.Sqrt(N(args.Count > 0 ? args[0] : DynValue.Nil))));
            // PICO-8 sin/cos use 0–1 turns; sin is negated
            s.Globals["sin"]   = DynValue.NewCallback((ctx, args) => { var x=N(args.Count>0?args[0]:DynValue.Nil); return DynValue.NewNumber(Math.Sin(x * -2 * Math.PI)); });
            s.Globals["cos"]   = DynValue.NewCallback((ctx, args) => { var x=N(args.Count>0?args[0]:DynValue.Nil); return DynValue.NewNumber(Math.Cos(x *  2 * Math.PI)); });
            s.Globals["atan2"] = DynValue.NewCallback((ctx, args) => { var y=N(args.Count>0?args[0]:DynValue.Nil); var x=N(args.Count>1?args[1]:DynValue.Nil); return DynValue.NewNumber(Math.Atan2(y,x) / (2*Math.PI)); });
            // min/max are nil-tolerant: nil is treated as the other argument's value
            s.Globals["min"] = DynValue.NewCallback((ctx, args) => {
                if (args.Count == 0) return DynValue.NewNumber(0);
                if (args.Count == 1 || args[1].IsNil()) return DynValue.NewNumber(N(args[0]));
                if (args[0].IsNil()) return DynValue.NewNumber(N(args[1]));
                return DynValue.NewNumber(Math.Min(N(args[0]), N(args[1])));
            });
            s.Globals["max"] = DynValue.NewCallback((ctx, args) => {
                if (args.Count == 0) return DynValue.NewNumber(0);
                if (args.Count == 1 || args[1].IsNil()) return DynValue.NewNumber(N(args[0]));
                if (args[0].IsNil()) return DynValue.NewNumber(N(args[1]));
                return DynValue.NewNumber(Math.Max(N(args[0]), N(args[1])));
            });
            s.Globals["mid"] = DynValue.NewCallback((ctx, args) => DynValue.NewNumber(MidFn(N(args.Count>0?args[0]:DynValue.Nil), N(args.Count>1?args[1]:DynValue.Nil), N(args.Count>2?args[2]:DynValue.Nil))));
            s.Globals["rnd"] = DynValue.NewCallback((ctx, args) => {
                if (args.Count == 0 || args[0].IsNil()) return DynValue.NewNumber(_rng.NextDouble());
                if (args[0].Type == DataType.Table) {
                    var tbl = args[0].Table; int len = tbl.Length;
                    if (len == 0) return DynValue.Nil;
                    return tbl.Get((int)(_rng.NextDouble() * len) + 1);
                }
                double x = N(args[0]);
                return DynValue.NewNumber(x == 0 ? 0 : _rng.NextDouble() * x);
            });
            Reg(s, "srand", (Func<double,double>)(x => { _rng = new Random((int)x); return 0; }));
            s.Globals["sgn"] = DynValue.NewCallback((ctx, args) => { var x = N(args.Count > 0 ? args[0] : DynValue.Nil); return DynValue.NewNumber(x < 0 ? -1 : x > 0 ? 1 : 0); });
            // Bitwise ops accept booleans (false=0, true=1) as well as numbers.
            // All ops treat values as 32-bit integers (PICO-8 is 16.16 fixed-point).
            s.Globals["band"]  = DynValue.NewCallback((ctx, args) => { var a=DynToLong(args[0]);var b=DynToLong(args[1]); return DynValue.NewNumber((int)a&(int)b); });
            s.Globals["bor"]   = DynValue.NewCallback((ctx, args) => { var a=DynToLong(args[0]);var b=DynToLong(args[1]); return DynValue.NewNumber((int)a|(int)b); });
            s.Globals["bxor"]  = DynValue.NewCallback((ctx, args) => { var a=DynToLong(args[0]);var b=DynToLong(args[1]); return DynValue.NewNumber((int)a^(int)b); });
            s.Globals["bnot"]  = DynValue.NewCallback((ctx, args) => { var a=DynToLong(args[0]); return DynValue.NewNumber((double)(int)~(uint)a); });
            s.Globals["shl"]   = DynValue.NewCallback((ctx, args) => { var a=DynToLong(args[0]);int b=(int)(DynToLong(args[1])&31); return DynValue.NewNumber((double)((int)a<<b)); });
            s.Globals["shr"]   = DynValue.NewCallback((ctx, args) => { var a=DynToLong(args[0]);int b=(int)(DynToLong(args[1])&31); return DynValue.NewNumber((double)((int)a>>b)); }); // arithmetic
            s.Globals["lshr"]  = DynValue.NewCallback((ctx, args) => { var a=DynToLong(args[0]);int b=(int)(DynToLong(args[1])&31); return DynValue.NewNumber((double)((uint)a>>b)); }); // logical
            s.Globals["rotl"]  = DynValue.NewCallback((ctx, args) => { var u=(uint)DynToLong(args[0]);int n=(int)(DynToLong(args[1])&31); return DynValue.NewNumber((double)(int)((u<<n)|(u>>(32-n)))); });
            s.Globals["rotr"]  = DynValue.NewCallback((ctx, args) => { var u=(uint)DynToLong(args[0]);int n=(int)(DynToLong(args[1])&31); return DynValue.NewNumber((double)(int)((u>>n)|(u<<(32-n)))); });
            s.Globals["stat"] = DynValue.NewCallback((ctx, args) =>
            {
                double n = args.Count > 0 ? N(args[0]) : 0;
                return DynValue.NewNumber(Stat(n));
            });
            Reg(s, "t",     (Func<double>)(() => Tick / 60.0));
            Reg(s, "time",  (Func<double>)(() => Tick / 60.0));
            // log(x[, base]) — natural log by default; PICO-8 0.2.1+
            Reg(s, "log",   (Func<double,DynValue,double>)((x,b) =>
                b.IsNil() ? Math.Log(x) : Math.Log(x, b.CastToNumber() ?? Math.E)));
            Reg(s, "exp",   (Func<double,double>)Math.Exp);
            Reg(s, "pow",   (Func<double,double,double>)Math.Pow);
            Reg(s, "powi",  (Func<double,double,double>)Math.Pow);

            // ── String helpers ────────────────────────────────────────────────
            Reg(s, "tostr", (Func<DynValue,DynValue,string>)Tostr);
            Reg(s, "tonum", (Func<DynValue,DynValue>)Tonum);
            s.Globals["sub"] = DynValue.NewCallback((ctx, args) => {
                string str = args.Count > 0 && args[0].Type == DataType.String ? args[0].String : (args.Count > 0 ? (args[0].CastToString() ?? "") : "");
                double from = args.Count > 1 ? N(args[1]) : 1;
                double to   = args.Count > 2 ? N(args[2]) : str.Length;
                return DynValue.NewString(Pico8Sub(str, from, to));
            });
            Reg(s, "chr",   (Func<double,string>)(n => ((char)(int)n).ToString()));
            Reg(s, "ord",   (Func<DynValue,DynValue,double>)Ord);
            Reg(s, "split", (Func<DynValue,DynValue,DynValue,DynValue>)Split);
            Reg(s, "count", (Func<DynValue,double>)(t =>
                t.Type == DataType.Table ? t.Table.Length : 0));
            // Expose string.* namespace (MoonSharp provides it in the soft sandbox)
            // but also alias common string functions as globals for PICO-8 compat
            Reg(s, "strformat", (Func<DynValue,DynValue,DynValue,DynValue,DynValue,string>)StrFormat);

            // ── Screen ───────────────────────────────────────────────────────
            Reg(s, "cls",      (Action<DynValue>)Cls);
            Reg(s, "flip",     (Action)Flip);

            // ── Pixel ops ────────────────────────────────────────────────────
            s.Globals["pset"]  = DynValue.NewCallback((ctx, a) => { Pset(N(a[0]),N(a[1]),a.Count>2?a[2]:DynValue.Nil); return DynValue.Nil; });
            s.Globals["pget"]  = DynValue.NewCallback((ctx, a) => DynValue.NewNumber(Pget(N(a[0]),N(a[1]))));
            s.Globals["color"] = DynValue.NewCallback((ctx, args) => { SetDrawColor(args.Count > 0 && !args[0].IsNil() ? N(args[0]) : 6); return DynValue.Nil; });

            // ── Lines ────────────────────────────────────────────────────────
            s.Globals["line"] = DynValue.NewCallback((ctx, args) => { LineVariadic(args); return DynValue.Nil; });

            // ── Shapes ───────────────────────────────────────────────────────
            DynValue Ga(CallbackArguments a,int i)=>a.Count>i?a[i]:DynValue.Nil;
            s.Globals["rect"]     = DynValue.NewCallback((ctx,a)=>{Rect(N(a[0]),N(a[1]),N(a[2]),N(a[3]),Ga(a,4));return DynValue.Nil;});
            s.Globals["rectfill"] = DynValue.NewCallback((ctx,a)=>{Rectfill(N(a[0]),N(a[1]),N(a[2]),N(a[3]),Ga(a,4));return DynValue.Nil;});
            s.Globals["circ"]     = DynValue.NewCallback((ctx,a)=>{Circ(N(a[0]),N(a[1]),N(a[2]),Ga(a,3));return DynValue.Nil;});
            s.Globals["circfill"] = DynValue.NewCallback((ctx,a)=>{Circfill(N(a[0]),N(a[1]),N(a[2]),Ga(a,3));return DynValue.Nil;});
            s.Globals["oval"]     = DynValue.NewCallback((ctx,a)=>{Oval(N(a[0]),N(a[1]),N(a[2]),N(a[3]),Ga(a,4));return DynValue.Nil;});
            s.Globals["ovalfill"] = DynValue.NewCallback((ctx,a)=>{Ovalfill(N(a[0]),N(a[1]),N(a[2]),N(a[3]),Ga(a,4));return DynValue.Nil;});

            // ── Sprites ──────────────────────────────────────────────────────
            s.Globals["spr"]  = DynValue.NewCallback((ctx,a)=>{Spr(N(a[0]),N(a[1]),N(a[2]),Ga(a,3),Ga(a,4),Ga(a,5),Ga(a,6));return DynValue.Nil;});
            s.Globals["sspr"] = DynValue.NewCallback((ctx,a)=>{Sspr(N(a[0]),N(a[1]),N(a[2]),N(a[3]),N(a[4]),N(a[5]),Ga(a,6),Ga(a,7),Ga(a,8),Ga(a,9));return DynValue.Nil;});
            s.Globals["sget"] = DynValue.NewCallback((ctx,a)=>DynValue.NewNumber(SgetApi(N(a[0]),N(a[1]))));
            s.Globals["sset"] = DynValue.NewCallback((ctx,a)=>{SsetApi(N(a[0]),N(a[1]),Ga(a,2));return DynValue.Nil;});
            s.Globals["fget"] = DynValue.NewCallback((ctx, args) => Fget(N(args.Count>0?args[0]:DynValue.Nil), args.Count>1?args[1]:DynValue.Nil));
            s.Globals["fset"] = DynValue.NewCallback((ctx, args) => { Fset(N(args.Count>0?args[0]:DynValue.Nil), args.Count>1?args[1]:DynValue.Nil, args.Count>2?args[2]:DynValue.Nil); return DynValue.Nil; });

            // ── Map ──────────────────────────────────────────────────────────
            s.Globals["map"] = DynValue.NewCallback((ctx, args) => { MapDrawVariadic(args); return DynValue.Nil; });
            s.Globals["mget"] = DynValue.NewCallback((ctx,a)=>DynValue.NewNumber(MgetApi(N(a[0]),N(a[1]))));
            s.Globals["mset"] = DynValue.NewCallback((ctx,a)=>{MsetApi(N(a[0]),N(a[1]),N(a[2]));return DynValue.Nil;});
            Reg(s, "tline",    (Action<double,double,double,double,double,double,DynValue,DynValue,DynValue>)Tline);

            // ── Palette / draw state ─────────────────────────────────────────
            s.Globals["pal"]    = DynValue.NewCallback((ctx, args) => { PalVariadic(args);  return DynValue.Nil; });
            s.Globals["palt"]   = DynValue.NewCallback((ctx, args) => { PaltVariadic(args); return DynValue.Nil; });
            Reg(s, "fillp",    (Action<DynValue>)Fillp);
            Reg(s, "clip",     (Action<DynValue,DynValue,DynValue,DynValue>)Clip);
            Reg(s, "camera",   (Action<DynValue,DynValue>)CameraSet);
            Reg(s, "cursor",   (Action<DynValue,DynValue,DynValue>)Cursor);

            // ── Text ─────────────────────────────────────────────────────────
            s.Globals["print"] = DynValue.NewCallback((ctx, args) => PrintVariadic(args));
            s.Globals["?"]     = s.Globals["print"];

            // ── Input ────────────────────────────────────────────────────────
            // btn/btnp: with button arg → bool; with no arg → bitfield integer
            s.Globals["btn"]  = DynValue.NewCallback((ctx, args) => {
                var arg = args.Count > 0 ? args[0] : DynValue.Nil;
                if (arg.IsNil()) {
                    int bits = 0; for (int b=0;b<_btnCurrent.Length;b++) if (_btnCurrent[b]) bits|=(1<<b);
                    return DynValue.NewNumber(bits);
                }
                int idx=(int)(arg.CastToNumber()??0);
                return DynValue.NewBoolean((uint)idx<(uint)_btnCurrent.Length && _btnCurrent[idx]);
            });
            s.Globals["btnp"] = DynValue.NewCallback((ctx, args) => {
                var arg = args.Count > 0 ? args[0] : DynValue.Nil;
                if (arg.IsNil()) {
                    int bits = 0; for (int b=0;b<_btnCurrent.Length;b++) if (_btnCurrent[b]&&!_btnPrevious[b]) bits|=(1<<b);
                    return DynValue.NewNumber(bits);
                }
                int idx=(int)(arg.CastToNumber()??0);
                return DynValue.NewBoolean((uint)idx<(uint)_btnCurrent.Length && _btnCurrent[idx]&&!_btnPrevious[idx]);
            });

            // ── Memory ───────────────────────────────────────────────────────
            s.Globals["peek"]  = DynValue.NewCallback((ctx,args)=>DynValue.NewNumber(PeekByte((int)N(args.Count>0?args[0]:DynValue.Nil))));
            s.Globals["poke"]  = DynValue.NewCallback((ctx,args)=>{ PokeByte((int)N(args.Count>0?args[0]:DynValue.Nil),(byte)(int)N(args.Count>1?args[1]:DynValue.Nil)); return DynValue.Nil; });
            s.Globals["peek2"] = DynValue.NewCallback((ctx,args)=>DynValue.NewNumber(PeekWord((int)N(args.Count>0?args[0]:DynValue.Nil))));
            s.Globals["poke2"] = DynValue.NewCallback((ctx,args)=>{ PokeWord((int)N(args.Count>0?args[0]:DynValue.Nil),(ushort)(int)N(args.Count>1?args[1]:DynValue.Nil)); return DynValue.Nil; });
            s.Globals["peek4"] = DynValue.NewCallback((ctx,args)=>DynValue.NewNumber(PeekDword((int)N(args.Count>0?args[0]:DynValue.Nil))));
            s.Globals["poke4"] = DynValue.NewCallback((ctx,args)=>{ PokeDword((int)N(args.Count>0?args[0]:DynValue.Nil),(uint)(long)N(args.Count>1?args[1]:DynValue.Nil)); return DynValue.Nil; });
            Reg(s, "memcpy", (Action<double,double,double>)((d,r,l) => MemCopy((int)d,(int)r,(int)l)));
            Reg(s, "memset", (Action<double,double,double>)((d,v,l) => MemSet((int)d,(byte)(int)v,(int)l)));
            Reg(s, "reload", (Action<DynValue,DynValue,DynValue,DynValue>)((a,b,c,d) => { /* no-op: no separate ROM */ }));
            Reg(s, "cstore", (Action<DynValue,DynValue,DynValue,DynValue>)((a,b,c,d) => { }));

            // ── Sound ────────────────────────────────────────────────────────
            s.Globals["sfx"] = DynValue.NewCallback((ctx, args) =>
            {
                int n       = args.Count > 0 ? (int)N(args[0]) : -1;
                int channel = args.Count > 1 ? (int)N(args[1]) : -1;
                int offset  = args.Count > 2 ? (int)N(args[2]) :  0;
                int length  = args.Count > 3 ? (int)N(args[3]) : -1;
                Audio?.SfxPlay(n, channel, offset, length);
                return DynValue.Nil;
            });
            s.Globals["music"] = DynValue.NewCallback((ctx, args) =>
            {
                int n      = args.Count > 0 ? (int)N(args[0]) : -1;
                int fade   = args.Count > 1 ? (int)N(args[1]) :  0;
                int chMask = args.Count > 2 ? (int)N(args[2]) :  0;
                Audio?.MusicPlay(n, fade, chMask);
                return DynValue.Nil;
            });

            // ── Persistent cartridge data (stubs — no persistent storage) ────
            s.Globals["cartdata"] = DynValue.NewCallback((ctx, args) => DynValue.Nil);
            s.Globals["dget"]     = DynValue.NewCallback((ctx, args) => DynValue.NewNumber(0));
            s.Globals["dset"]     = DynValue.NewCallback((ctx, args) => DynValue.Nil);

            // ── System stubs ──────────────────────────────────────────────────
            s.Globals["menuitem"] = DynValue.NewCallback((ctx, args) => DynValue.Nil);
            s.Globals["extcmd"]   = DynValue.NewCallback((ctx, args) => DynValue.Nil);

            // ── Map aliases ───────────────────────────────────────────────────
            // mapdraw is the old PICO-8 name for map()
            s.Globals["mapdraw"] = s.Globals["map"];

            // ── Coroutines (wrap MoonSharp's native coroutine support) ────────
            s.DoString(@"
function cocreate(f) return coroutine.create(f) end
function coresume(co,...) return coroutine.resume(co,...) end
function costatus(co) return coroutine.status(co) end
function yield(...) return coroutine.yield(...) end
");

            // ── Debug ────────────────────────────────────────────────────────
            Reg(s, "printh", (Action<DynValue>)(v => Console.WriteLine(DynToString(v))));

            // ── Table / string / misc helpers in Lua ─────────────────────────
            s.DoString(@"
function all(t)
    if t == nil then return function() end end
    if type(t) ~= 'table' then return function() end end
    local i,n = 0,#t
    return function() i=i+1; if i<=n then return t[i] end end
end
function add(t, v, i)
    if t == nil then return end
    if i then table.insert(t, i, v) else t[#t+1]=v end
    return v
end
function del(t, v)
    if t == nil then return end
    for i=1,#t do if t[i]==v then table.remove(t,i) return end end
end
function deli(t, i) if t then table.remove(t, i) end end
function count(t) if type(t)=='string' then return #t end return #(t or {}) end
function foreach(t, f) for v in all(t) do f(v) end end
function tostring(v) return tostr(v) end

-- Minimal string-class shim so carts can do ('hello'):upper() etc.
-- MoonSharp provides the full string metatable so this is mostly a no-op bridge.

-- PICO-8 integer / bitwise helpers
function __idiv(a,b) return flr(a/b) end
function intop(op,a,b)
    if op=='+'  then return a+b
    elseif op=='-'  then return a-b
    elseif op=='*'  then return a*b
    elseif op=='/'  then return flr(a/b)
    elseif op=='%'  then return a%b
    elseif op=='&'  then return band(a,b)
    elseif op=='|'  then return bor(a,b)
    elseif op=='^^' then return bxor(a,b)
    elseif op=='~'  then return bnot(a)
    elseif op=='<<' then return shl(a,b)
    elseif op=='>>' then return shr(a,b)
    elseif op=='>>>' then return lshr(a,b)
    elseif op=='rotl' then return rotl(a,b)
    elseif op=='rotr' then return rotr(a,b)
    end
end
");

            return s;
        }

        private static void Reg(Script s, string name, Delegate d) => s.Globals[name] = d;
        private DynValue? TryGetFn(string name)
        {
            var v = _lua.Globals.Get(name);
            return v.Type == DataType.Function ? v : null;
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Pixel writing ────────────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════

        // For primitive drawing (line, rect, circ, pset…): applies camera, clip, fillp, drawPal.
        private void PrimPut(int wx, int wy, byte col)
        {
            int x = wx - _camX, y = wy - _camY;
            if ((uint)x >= (uint)ScreenWidth || (uint)y >= (uint)ScreenHeight) return;
            if (x < _clipX || x >= _clipX + _clipW || y < _clipY || y >= _clipY + _clipH) return;

            byte final;
            if (_fillpActive)
            {
                int px = ((x % 4) + 4) % 4, py = ((y % 4) + 4) % 4;
                int bit = 15 - px - py * 4;
                bool on = (_fillPattern & (1 << bit)) != 0;
                if (on)
                    final = _drawPal[col & 0xF];
                else if (_fillpSecondaryOpaque)
                    final = _drawPal[_fillpSecondaryColor & 0xF];
                else
                    return;
            }
            else
            {
                final = _drawPal[col & 0xF];
            }
            Screen[y * ScreenWidth + x] = final;
        }

        // For sprite/map drawing: applies camera, clip, transPal, drawPal. No fillp.
        private void SprPut(int wx, int wy, byte rawCol)
        {
            if (_transPal[rawCol & 0xF]) return;
            int x = wx - _camX, y = wy - _camY;
            if ((uint)x >= (uint)ScreenWidth || (uint)y >= (uint)ScreenHeight) return;
            if (x < _clipX || x >= _clipX + _clipW || y < _clipY || y >= _clipY + _clipH) return;
            Screen[y * ScreenWidth + x] = _drawPal[rawCol & 0xF];
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Screen ops ───────────────────────────────────────────════════════
        // ════════════════════════════════════════════════════════════════════

        private void Cls(DynValue c)
        {
            byte col = c.IsNil() ? (byte)0 : (byte)((int)(c.CastToNumber() ?? 0) & 0xF);
            Array.Fill(Screen, col);
            _cursorX = 0; _cursorY = 0;
        }
        private void Flip() { /* manual flip – no-op since we auto-present every frame */ }

        // ════════════════════════════════════════════════════════════════════
        // ── Pixel ops ────────────────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════

        private void Pset(double x, double y, DynValue c)
        {
            byte col = c.IsNil() ? _drawColor : (byte)((int)(c.CastToNumber() ?? _drawColor) & 0xF);
            if (!c.IsNil()) _drawColor = col;
            PrimPut((int)x, (int)y, col);
        }

        private double Pget(double x, double y)
        {
            int sx = (int)x - _camX, sy = (int)y - _camY;
            if ((uint)sx >= ScreenWidth || (uint)sy >= ScreenHeight) return 0;
            return Screen[sy * ScreenWidth + sx];
        }

        private void SetDrawColor(double c) => _drawColor = (byte)((int)c & 0xF);

        // ════════════════════════════════════════════════════════════════════
        // ── Lines ────────────────────────────────════════════════════════════
        // ════════════════════════════════════════════════════════════════════

        private void LineVariadic(CallbackArguments args)
        {
            // line()                         → clears stored endpoint
            // line(x1,y1)                    → from last pt to (x1,y1) with current color
            // line(x1,y1,c)                  → from last pt to (x1,y1) with color c
            // line(x0,y0,x1,y1)              → from (x0,y0) to (x1,y1)
            // line(x0,y0,x1,y1,c)            → from (x0,y0) to (x1,y1) with color c

            int n = args.Count;
            if (n == 0) { _lineX = double.NaN; _lineY = double.NaN; return; }

            double a0 = args[0].IsNil() ? 0 : (args[0].CastToNumber() ?? 0);
            double a1 = n > 1 ? (args[1].CastToNumber() ?? 0) : 0;

            if (n <= 3) // from last point
            {
                byte col = n == 3 ? ColorArg(args[2]) : _drawColor;
                if (!double.IsNaN(_lineX))
                    DrawLinePrim((int)_lineX, (int)_lineY, (int)a0, (int)a1, col);
                _lineX = a0; _lineY = a1;
                if (n == 3) _drawColor = col;
            }
            else // 4 or 5 args: explicit start point
            {
                double a2 = args[2].CastToNumber() ?? 0;
                double a3 = args[3].CastToNumber() ?? 0;
                byte col  = n >= 5 ? ColorArg(args[4]) : _drawColor;
                DrawLinePrim((int)a0, (int)a1, (int)a2, (int)a3, col);
                _lineX = a2; _lineY = a3;
                if (n >= 5) _drawColor = col;
            }
        }

        private void DrawLinePrim(int x0, int y0, int x1, int y1, byte col)
        {
            int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                PrimPut(x0, y0, col);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Shapes ───────────────────════════════════════════════════════════
        // ════════════════════════════════════════════════════════════════════

        private void Rect(double x0, double y0, double x1, double y1, DynValue c)
        {
            byte col = ColorDyn(c);
            int ix0=(int)x0,iy0=(int)y0,ix1=(int)x1,iy1=(int)y1;
            DrawLinePrim(ix0,iy0,ix1,iy0,col);
            DrawLinePrim(ix1,iy0,ix1,iy1,col);
            DrawLinePrim(ix1,iy1,ix0,iy1,col);
            DrawLinePrim(ix0,iy1,ix0,iy0,col);
        }

        private void Rectfill(double x0, double y0, double x1, double y1, DynValue c)
        {
            byte col = ColorDyn(c);
            int lx=(int)Math.Min(x0,x1), rx=(int)Math.Max(x0,x1);
            int ty=(int)Math.Min(y0,y1), by=(int)Math.Max(y0,y1);
            for (int y=ty; y<=by; y++)
                for (int x=lx; x<=rx; x++)
                    PrimPut(x,y,col);
        }

        private void Circ(double cx, double cy, double r, DynValue c)
            => DrawEllipsePx((int)cx,(int)cy,(int)r,(int)r, ColorDyn(c), false);

        private void Circfill(double cx, double cy, double r, DynValue c)
            => DrawEllipsePx((int)cx,(int)cy,(int)r,(int)r, ColorDyn(c), true);

        private void Oval(double x0, double y0, double x1, double y1, DynValue c)
        {
            int rx=(int)Math.Abs(x1-x0)/2, ry=(int)Math.Abs(y1-y0)/2;
            int cx=(int)(x0+x1)/2,          cy=(int)(y0+y1)/2;
            DrawEllipsePx(cx, cy, rx, ry, ColorDyn(c), false);
        }

        private void Ovalfill(double x0, double y0, double x1, double y1, DynValue c)
        {
            int rx=(int)Math.Abs(x1-x0)/2, ry=(int)Math.Abs(y1-y0)/2;
            int cx=(int)(x0+x1)/2,          cy=(int)(y0+y1)/2;
            DrawEllipsePx(cx, cy, rx, ry, ColorDyn(c), true);
        }

        // Midpoint ellipse algorithm – also handles circles (rx==ry).
        private void DrawEllipsePx(int cx, int cy, int rx, int ry, byte col, bool fill)
        {
            if (rx < 0) rx = 0;
            if (ry < 0) ry = 0;

            if (rx == 0 && ry == 0) { PrimPut(cx,cy,col); return; }
            if (rx == 0) { DrawLinePrim(cx,cy-ry,cx,cy+ry,col); return; }
            if (ry == 0) { DrawLinePrim(cx-rx,cy,cx+rx,cy,col); return; }

            long rx2=(long)rx*rx, ry2=(long)ry*ry;
            long twoRx2=2*rx2, twoRy2=2*ry2;
            int x=0, y=ry;
            long px=0, py=twoRx2*y;
            long p=(long)(ry2 - rx2*ry + 0.25*rx2);

            while (px < py)
            {
                PlotEllipsePoints(cx,cy,x,y,col,fill);
                x++; px+=twoRy2;
                if (p<0) p+=ry2+px;
                else { y--; py-=twoRx2; p+=ry2+px-py; }
            }
            p=(long)(ry2*(x+0.5)*(x+0.5)+rx2*(y-1)*(y-1)-rx2*ry2);
            while (y>=0)
            {
                PlotEllipsePoints(cx,cy,x,y,col,fill);
                y--; py-=twoRx2;
                if (p>0) p+=rx2-py;
                else { x++; px+=twoRy2; p+=rx2-py+px; }
            }
        }

        private void PlotEllipsePoints(int cx, int cy, int x, int y, byte col, bool fill)
        {
            if (fill)
            {
                DrawLinePrim(cx-x, cy+y, cx+x, cy+y, col);
                if (y!=0) DrawLinePrim(cx-x, cy-y, cx+x, cy-y, col);
            }
            else
            {
                PrimPut(cx+x,cy+y,col); PrimPut(cx-x,cy+y,col);
                PrimPut(cx+x,cy-y,col); PrimPut(cx-x,cy-y,col);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Sprites ──────────────────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════

        private void Spr(double n, double x, double y,
            DynValue w, DynValue h, DynValue flipX, DynValue flipY)
        {
            int sw = w.IsNil() ? 1 : (int)(w.CastToNumber() ?? 1);
            int sh = h.IsNil() ? 1 : (int)(h.CastToNumber() ?? 1);
            bool fx = !flipX.IsNil() && (flipX.CastToNumber() ?? 0) != 0;
            bool fy = !flipY.IsNil() && (flipY.CastToNumber() ?? 0) != 0;
            int idx = (int)n;
            int srcX = (idx % 16) * 8, srcY = (idx / 16) * 8;
            BlitSprite(srcX, srcY, sw*8, sh*8, (int)x, (int)y, fx, fy);
        }

        private void Sspr(double sx, double sy, double sw, double sh,
            double dx, double dy, DynValue dw, DynValue dh, DynValue flipX, DynValue flipY)
        {
            int destW = dw.IsNil() ? (int)sw : (int)(dw.CastToNumber() ?? sw);
            int destH = dh.IsNil() ? (int)sh : (int)(dh.CastToNumber() ?? sh);
            bool fx = !flipX.IsNil() && (flipX.CastToNumber() ?? 0) != 0;
            bool fy = !flipY.IsNil() && (flipY.CastToNumber() ?? 0) != 0;
            BlitSpriteScaled((int)sx,(int)sy,(int)sw,(int)sh,(int)dx,(int)dy,destW,destH,fx,fy);
        }

        private void BlitSprite(int srcX, int srcY, int sw, int sh,
                                int dstX, int dstY, bool fx, bool fy)
        {
            for (int py=0; py<sh; py++)
            for (int px=0; px<sw; px++)
            {
                int ssX = fx ? srcX+sw-1-px : srcX+px;
                int ssY = fy ? srcY+sh-1-py : srcY+py;
                SprPut(dstX+px, dstY+py, SprGet(ssX, ssY));
            }
        }

        private void BlitSpriteScaled(int srcX, int srcY, int srcW, int srcH,
                                      int dstX, int dstY, int dstW, int dstH,
                                      bool fx, bool fy)
        {
            if (dstW<=0 || dstH<=0) return;
            for (int py=0; py<dstH; py++)
            for (int px=0; px<dstW; px++)
            {
                int ssX = srcX + (fx ? srcW-1 - px*srcW/dstW : px*srcW/dstW);
                int ssY = srcY + (fy ? srcH-1 - py*srcH/dstH : py*srcH/dstH);
                SprPut(dstX+px, dstY+py, SprGet(ssX, ssY));
            }
        }

        private byte SprGet(int x, int y)
        {
            if ((uint)x>=128 || (uint)y>=128) return 0;
            return _spriteSheet[y*128+x];
        }

        private double SgetApi(double x, double y) => SprGet((int)x,(int)y);
        private void   SsetApi(double x, double y, DynValue c)
        {
            int ix=(int)x, iy=(int)y;
            if ((uint)ix>=128 || (uint)iy>=128) return;
            byte col = c.IsNil() ? _drawColor : (byte)((int)(c.CastToNumber()??_drawColor)&0xF);
            _spriteSheet[iy*128+ix] = col;
        }

        // ── Sprite flags ──────────────────────────────────────────────────────
        private DynValue Fget(double n, DynValue flag)
        {
            int idx = (int)n & 0xFF;
            if (flag.IsNil()) return DynValue.NewNumber(_spriteFlags[idx]);
            int bit = (int)(flag.CastToNumber() ?? 0);
            return DynValue.NewBoolean((_spriteFlags[idx] & (1<<bit)) != 0);
        }

        private void Fset(double n, DynValue flag, DynValue val)
        {
            int idx = (int)n & 0xFF;
            if (flag.IsNil())
            {
                _spriteFlags[idx] = (byte)(val.CastToNumber() ?? 0);
                return;
            }
            int bit = (int)(flag.CastToNumber() ?? 0);
            bool on  = val.IsNil() ? true : (val.CastToNumber() ?? 1) != 0;
            if (on) _spriteFlags[idx] |=  (byte)(1<<bit);
            else    _spriteFlags[idx] &= (byte)~(1<<bit);
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Map ──────────────────────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════

        private void MapDrawVariadic(CallbackArguments args)
        {
            // map(cel_x, cel_y, screen_x, screen_y, cel_w, cel_h [, layer])
            if (args.Count < 6) return;
            double celX = args[0].CastToNumber() ?? 0;
            double celY = args[1].CastToNumber() ?? 0;
            double scrX = args[2].CastToNumber() ?? 0;
            double scrY = args[3].CastToNumber() ?? 0;
            double celW = args[4].CastToNumber() ?? 0;
            double celH = args[5].CastToNumber() ?? 0;
            int    layer = args.Count>6 ? (int)(args[6].CastToNumber()??0) : 0;

            for (int ty=0; ty<(int)celH; ty++)
            for (int tx=0; tx<(int)celW; tx++)
            {
                int mx=(int)celX+tx, my=(int)celY+ty;
                byte tile = MapGet(mx, my);
                if (tile==0) continue;
                if (layer!=0 && (_spriteFlags[tile] & layer)==0) continue;

                int sprX=(tile%16)*8, sprY=(tile/16)*8;
                BlitSprite(sprX, sprY, 8, 8, (int)scrX+tx*8, (int)scrY+ty*8, false, false);
            }
        }

        private double MgetApi(double x, double y) => MapGet((int)x,(int)y);
        private byte   MapGet(int x, int y)
        {
            if ((uint)x>=128 || (uint)y>=64) return 0;
            return _mapData[y*128+x];
        }
        private void MsetApi(double x, double y, double v)
        {
            int ix=(int)x, iy=(int)y;
            if ((uint)ix>=128 || (uint)iy>=64) return;
            _mapData[iy*128+ix] = (byte)v;
        }

        // ── Texture-mapped line ───────────────────────────────────────────────
        private void Tline(double x0, double y0, double x1, double y1,
                           double mx, double my,
                           DynValue mdxV, DynValue mdyV, DynValue layersV)
        {
            int sx0=(int)x0, sy0=(int)y0, sx1=(int)x1, sy1=(int)y1;
            int layers = layersV.IsNil() ? 0 : (int)(layersV.CastToNumber()??0);

            int ddx=sx1-sx0, ddy=sy1-sy0;
            int steps = Math.Max(Math.Abs(ddx), Math.Abs(ddy));
            if (steps==0) steps=1;

            double mdx = mdxV.IsNil() ? 1.0/8.0 : (mdxV.CastToNumber()??1.0/8.0);
            double mdy = mdyV.IsNil() ? 0.0      : (mdyV.CastToNumber()??0.0);

            double cmx=mx, cmy=my;
            for (int i=0; i<=steps; i++)
            {
                int sx = sx0 + (int)Math.Round((double)i*ddx/steps);
                int sy = sy0 + (int)Math.Round((double)i*ddy/steps);

                int tx = ((int)Math.Floor(cmx) % 128 + 128) % 128;
                int ty = ((int)Math.Floor(cmy) %  64 +  64) %  64;

                byte tileId = MapGet(tx, ty);
                bool draw = tileId!=0 && (layers==0 || (_spriteFlags[tileId]&layers)!=0);
                if (draw)
                {
                    double fx = ((cmx % 1.0)+1.0)%1.0;
                    double fy = ((cmy % 1.0)+1.0)%1.0;
                    int px = (int)(fx*8)+(tileId%16)*8;
                    int py = (int)(fy*8)+(tileId/16)*8;
                    SprPut(sx, sy, SprGet(px, py));
                }
                cmx+=mdx; cmy+=mdy;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Palette / draw state ──────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════

        private void PalVariadic(CallbackArguments args)
        {
            if (args.Count==0 || args[0].IsNil())
            {
                // Reset everything
                for (int i=0;i<16;i++) { _drawPal[i]=(byte)i; DisplayPalette[i]=(byte)i; }
                ResetTransPal();
                return;
            }
            int c0  = (int)(args[0].CastToNumber()??0) & 0xF;
            int c1  = (int)(args.Count>1 ? args[1].CastToNumber()??0 : 0) & 0xFF; // allow 0-255 for secret cols
            int mode= args.Count>2 ? (int)(args[2].CastToNumber()??0) : 0;

            if (mode==1) DisplayPalette[c0] = (byte)(c1 & 0xF);
            else         _drawPal[c0]       = (byte)(c1 & 0xF);
        }

        private void PaltVariadic(CallbackArguments args)
        {
            if (args.Count==0 || args[0].IsNil())
            {
                ResetTransPal();
                return;
            }
            int c = (int)(args[0].CastToNumber()??0) & 0xF;
            bool t = args.Count>1 ? (args[1].CastToNumber()??1)!=0 : true;
            _transPal[c] = t;
        }

        private void ResetTransPal()
        {
            Array.Clear(_transPal, 0, 16);
            _transPal[0] = true;
        }

        // fillp(p)
        // p's integer part = 16-bit dither mask (bit15=top-left).
        // p's fractional part >= 0.5  →  secondary color mode enabled.
        // When secondary color mode is on, the high nibble of the draw color is the secondary color.
        private void Fillp(DynValue pv)
        {
            if (pv.IsNil())
            {
                _fillpActive = false;
                return;
            }
            double p = pv.CastToNumber() ?? 0;
            double intPart  = Math.Floor(p);
            double fracPart = p - intPart;

            _fillPattern          = (ushort)((long)intPart & 0xFFFF);
            _fillpSecondaryOpaque = fracPart >= 0.5;
            _fillpActive          = true;
        }

        // When fillp secondary-color mode is active, the high nibble of the draw-color arg
        // is the secondary color. This is called before drawing filled shapes.
        private (byte primary, byte secondary) ExtractFillColors(byte rawCol)
        {
            if (_fillpSecondaryOpaque)
            {
                byte primary   = (byte)(rawCol & 0xF);
                byte secondary = (byte)((rawCol >> 4) & 0xF);
                return (primary, secondary);
            }
            return ((byte)(rawCol & 0xF), 0);
        }

        private void Clip(DynValue x, DynValue y, DynValue w, DynValue h)
        {
            if (x.IsNil()) { _clipX=0;_clipY=0;_clipW=ScreenWidth;_clipH=ScreenHeight; return; }
            _clipX=(int)(x.CastToNumber()??0); _clipY=(int)(y.CastToNumber()??0);
            _clipW=(int)(w.CastToNumber()??ScreenWidth); _clipH=(int)(h.CastToNumber()??ScreenHeight);
        }

        private void CameraSet(DynValue x, DynValue y)
        {
            _camX = x.IsNil() ? 0 : (int)(x.CastToNumber()??0);
            _camY = y.IsNil() ? 0 : (int)(y.CastToNumber()??0);
        }

        private void Cursor(DynValue x, DynValue y, DynValue c)
        {
            if (!x.IsNil()) _cursorX = (int)(x.CastToNumber()??0);
            if (!y.IsNil()) _cursorY = (int)(y.CastToNumber()??0);
            if (!c.IsNil()) _drawColor = (byte)((int)(c.CastToNumber()??_drawColor)&0xF);
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Text ─────────────────────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════

        // Returns DynValue(x) – the x coordinate after the last character.
        private DynValue PrintVariadic(CallbackArguments args)
        {
            if (args.Count == 0) return DynValue.Nil;

            string str = DynToString(args[0]);
            ConsoleLog(str);
            int px, py;
            byte col;

            if (args.Count >= 3 && !args[1].IsNil())
            {
                px  = (int)(args[1].CastToNumber() ?? 0);
                py  = (int)(args[2].CastToNumber() ?? 0);
                col = args.Count>=4 ? ColorArg(args[3]) : _drawColor;
            }
            else
            {
                px  = _cursorX;
                py  = _cursorY;
                col = _drawColor;
                _cursorY += 6; // advance for next print()
            }

            int endX = PrintAt(str, px, py, col);
            return DynValue.NewNumber(endX);
        }

        // Returns the x position after the last char drawn.
        private int PrintAt(string text, int x, int y, byte col)
        {
            int cx = x;
            foreach (char ch in text)
            {
                if (ch == '\n') { cx = x; y += 6; continue; }
                DrawChar(ch, cx, y, col);
                cx += 4;
            }
            return cx;
        }

        private void DrawChar(char c, int x, int y, byte col)
        {
            if (!Font.Glyphs.TryGetValue(c, out var glyph)) return;
            for (int row=0; row<glyph.Length; row++)
            {
                byte bits = glyph[row];
                for (int col2=0; col2<3; col2++)
                    if ((bits & (1 << (2-col2))) != 0)
                        PrimPut(x+col2, y+row, col);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Input ────────────────────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════

        private bool BtnApi(DynValue i, DynValue player)
        {
            if (i.IsNil()) return false; // TODO: could return a bitmask
            int idx = (int)(i.CastToNumber()??0);
            if ((uint)idx >= (uint)_btnCurrent.Length) return false;
            return _btnCurrent[idx];
        }

        private bool BtnpApi(DynValue i, DynValue player)
        {
            if (i.IsNil()) return false;
            int idx = (int)(i.CastToNumber()??0);
            if ((uint)idx >= (uint)_btnCurrent.Length) return false;
            return _btnCurrent[idx] && !_btnPrevious[idx];
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Memory (peek / poke) ──────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════
        //
        // PICO-8 RAM layout (abbreviated):
        //  0x0000-0x1FFF  Sprite sheet (4bpp packed, 2px/byte, 128×128px)
        //  0x2000-0x2FFF  Map rows 0-31  (1 byte/tile, 128 cols)
        //  0x3000-0x30FF  Sprite flags   (1 byte/sprite)
        //  0x4300-0x5DFF  General-purpose RAM
        //  0x5F00-0x5F0F  Draw palette
        //  0x5F10-0x5F1F  Display palette
        //  0x5F20-0x5F23  Clip x0,y0,x1,y1
        //  0x5F25         Draw color
        //  0x5F26-0x5F27  Cursor x,y
        //  0x5F28-0x5F2B  Camera x (lo,hi), y (lo,hi)
        //  0x5F54-0x5F57  Fill pattern (32-bit, low 16 bits = pattern)
        //  0x6000-0x7FFF  Screen (4bpp packed, 2px/byte)
        //
        // NOTE: 0x1000-0x1FFF is sprite sheet rows 64-127 AND map rows 32-63
        //       (shared in real hardware). We serve it as sprite sheet only.

        private byte PeekByte(int addr)
        {
            addr &= 0x7FFF;
            if (addr < 0x2000) // Sprite sheet (4bpp)
            {
                int pi=addr*2, x=pi%128, y=pi/128;
                if (y>=128) return 0;
                byte lo=SprGet(x,y), hi=SprGet(x+1,y);
                return (byte)((lo&0xF)|((hi&0xF)<<4));
            }
            if (addr < 0x3000) // Map rows 0-31
            {
                int mi=addr-0x2000;
                return mi<_mapData.Length ? _mapData[mi] : (byte)0;
            }
            if (addr < 0x3100) return _spriteFlags[addr-0x3000]; // Sprite flags
            if (addr >= 0x4300 && addr < 0x5E00)                 // GP RAM
            {
                int gi=addr-0x4300;
                return gi<_gpRam.Length ? _gpRam[gi] : (byte)0;
            }
            if (addr >= 0x5F00 && addr < 0x5F10) return _drawPal[addr-0x5F00];
            if (addr >= 0x5F10 && addr < 0x5F20) return DisplayPalette[addr-0x5F10];
            if (addr == 0x5F20) return (byte)_clipX;
            if (addr == 0x5F21) return (byte)_clipY;
            if (addr == 0x5F22) return (byte)(_clipX+_clipW);
            if (addr == 0x5F23) return (byte)(_clipY+_clipH);
            if (addr == 0x5F25) return _drawColor;
            if (addr == 0x5F26) return (byte)_cursorX;
            if (addr == 0x5F27) return (byte)_cursorY;
            if (addr == 0x5F28) return (byte)(_camX & 0xFF);
            if (addr == 0x5F29) return (byte)((_camX>>8)&0xFF);
            if (addr == 0x5F2A) return (byte)(_camY & 0xFF);
            if (addr == 0x5F2B) return (byte)((_camY>>8)&0xFF);
            if (addr >= 0x5F54 && addr <= 0x5F57)
            {
                uint p = (uint)_fillPattern;
                return (byte)((p >> ((addr-0x5F54)*8)) & 0xFF);
            }
            if (addr >= 0x6000) // Screen (4bpp)
            {
                int pi=(addr-0x6000)*2, x=pi%128, y=pi/128;
                if (y>=128) return 0;
                byte lo=Screen[y*128+x], hi= x+1<128 ? Screen[y*128+x+1] : (byte)0;
                return (byte)((lo&0xF)|((hi&0xF)<<4));
            }
            return 0;
        }

        private void PokeByte(int addr, byte val)
        {
            addr &= 0x7FFF;
            if (addr < 0x2000) // Sprite sheet (4bpp)
            {
                int pi=addr*2, x=pi%128, y=pi/128;
                if (y>=128) return;
                if (x   <128) _spriteSheet[y*128+x  ] = (byte)(val & 0xF);
                if (x+1 <128) _spriteSheet[y*128+x+1] = (byte)((val>>4)&0xF);
                return;
            }
            if (addr < 0x3000) { int mi=addr-0x2000; if(mi<_mapData.Length) _mapData[mi]=val; return; }
            if (addr < 0x3100) { _spriteFlags[addr-0x3000]=val; return; }
            if (addr >= 0x4300 && addr < 0x5E00) { int gi=addr-0x4300; if(gi<_gpRam.Length)_gpRam[gi]=val; return; }
            if (addr >= 0x5F00 && addr < 0x5F10) { _drawPal[addr-0x5F00]=val; return; }
            if (addr >= 0x5F10 && addr < 0x5F20) { DisplayPalette[addr-0x5F10]=val; return; }
            if (addr == 0x5F25) { _drawColor=(byte)(val&0xF); return; }
            if (addr == 0x5F26) { _cursorX=val; return; }
            if (addr == 0x5F27) { _cursorY=val; return; }
            if (addr >= 0x5F54 && addr <= 0x5F57)
            {
                int shift=(addr-0x5F54)*8;
                _fillPattern=(ushort)((_fillPattern & ~(0xFF<<shift))|(val<<shift));
                return;
            }
            if (addr >= 0x6000) // Screen (4bpp)
            {
                int pi=(addr-0x6000)*2, x=pi%128, y=pi/128;
                if (y>=128) return;
                if (x   <128) Screen[y*128+x  ] = (byte)(val & 0xF);
                if (x+1 <128) Screen[y*128+x+1] = (byte)((val>>4)&0xF);
            }
        }

        private double PeekWord(int addr)
        {
            return PeekByte(addr) | (PeekByte(addr+1)<<8);
        }
        private void PokeWord(int addr, ushort val)
        {
            PokeByte(addr, (byte)(val & 0xFF));
            PokeByte(addr+1, (byte)(val>>8));
        }
        private double PeekDword(int addr)
        {
            uint v=(uint)PeekByte(addr)|(uint)(PeekByte(addr+1)<<8)
                  |(uint)(PeekByte(addr+2)<<16)|(uint)(PeekByte(addr+3)<<24);
            return v;
        }
        private void PokeDword(int addr, uint val)
        {
            PokeByte(addr,   (byte)(val      &0xFF));
            PokeByte(addr+1, (byte)((val>> 8)&0xFF));
            PokeByte(addr+2, (byte)((val>>16)&0xFF));
            PokeByte(addr+3, (byte)((val>>24)&0xFF));
        }

        private void MemCopy(int dst, int src, int len)
        {
            for (int i=0; i<len; i++) PokeByte(dst+i, PeekByte(src+i));
        }
        private void MemSet(int dst, byte val, int len)
        {
            for (int i=0; i<len; i++) PokeByte(dst+i, val);
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Misc ─────────────────────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════

        private double Stat(double n)
        {
            switch ((int)n)
            {
                case 0:  return Tick;
                case 1:  return 0;  // CPU usage – stub
                // Sound/music status — legacy (16-26) and current (46-56) both supported
                case 16: case 46: return Audio?.ChannelSfx(0)  ?? -1;
                case 17: case 47: return Audio?.ChannelSfx(1)  ?? -1;
                case 18: case 48: return Audio?.ChannelSfx(2)  ?? -1;
                case 19: case 49: return Audio?.ChannelSfx(3)  ?? -1;
                case 20: case 50: return Audio?.ChannelNote(0) ?? -1;
                case 21: case 51: return Audio?.ChannelNote(1) ?? -1;
                case 22: case 52: return Audio?.ChannelNote(2) ?? -1;
                case 23: case 53: return Audio?.ChannelNote(3) ?? -1;
                case 24: case 54: return Audio?.MusicPattern() ?? -1;
                case 25: case 55: return 0;  // reserved
                case 26: case 56: return Audio?.PatternTicks() ?? 0;
                // Mouse/devkit (stub)
                case 30: return 0;
                case 31: return 0;
                case 32: return 0;  // mouse x
                case 33: return 0;  // mouse y
                case 34: return 0;  // mouse buttons
                case 35: return 0;
                case 36: return 0;
                default: return 0;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // ── Lua preprocessor ───────────────────────────────────────────────────
        //
        // PICO-8 extends Lua with operators MoonSharp doesn't accept:
        //   &  |  ^^  >>  >>>  <<   →  band/bor/bxor/shr/lshr/shl
        //   \  //                   →  __idiv  (floor division)
        //   ~  (unary)              →  bnot
        //   0b...                   →  decimal literal
        //   !=                      →  ~=
        //
        // Strategy: tokenise first so strings/comments are never touched,
        // then do a token-level rewrite pass for each operator (lowest PICO-8
        // precedence first so left-associativity comes out correctly).
        // ══════════════════════════════════════════════════════════════════════

        // ── Token types ──────────────────────────────────────────────────────
        private enum LTT { Space, Comment, Str, Num, Id, Op, Punct }
        private readonly struct LTok
        {
            public readonly LTT  T;
            public readonly string S;
            public LTok(LTT t, string s) { T = t; S = s; }
        }

        // Keywords that terminate an expression going *backward* (LHS scan)
        private static readonly System.Collections.Generic.HashSet<string> _lkBack =
            new System.Collections.Generic.HashSet<string>{
                "and","or","not","then","do","else","elseif","end","until",
                "return","break","local","function","if","while","for","repeat","in"};

        // Keywords that terminate an expression going *forward* (RHS scan)
        private static readonly System.Collections.Generic.HashSet<string> _lkFwd =
            new System.Collections.Generic.HashSet<string>{
                "and","or","then","do","else","elseif","end","until","return","break"};

        // Operators that are LOWER precedence than any bitwise op in PICO-8 –
        // comparisons and concatenation stop both LHS and RHS scans.
        private static readonly System.Collections.Generic.HashSet<string> _lopStop =
            new System.Collections.Generic.HashSet<string>{
                "<",">","<=",">=","==","~=","!=",".."};

        // ── Tokeniser ─────────────────────────────────────────────────────────
        private static System.Collections.Generic.List<LTok> LuaLex(string src)
        {
            var list = new System.Collections.Generic.List<LTok>(src.Length / 4 + 8);
            int i = 0, n = src.Length;
            while (i < n)
            {
                char c = src[i];

                // Whitespace
                if (c <= ' ')
                {
                    int j = i + 1;
                    while (j < n && src[j] <= ' ') j++;
                    list.Add(new LTok(LTT.Space, src.Substring(i, j - i)));
                    i = j; continue;
                }

                // Comments
                if (c == '-' && i + 1 < n && src[i + 1] == '-')
                {
                    // Long comment --[=*[
                    if (i + 2 < n && src[i + 2] == '[')
                    {
                        int k = i + 3, eq = 0;
                        while (k < n && src[k] == '=') { eq++; k++; }
                        if (k < n && src[k] == '[')
                        {
                            k++;
                            string close = "]" + new string('=', eq) + "]";
                            int end = src.IndexOf(close, k);
                            if (end < 0) end = n - close.Length;
                            end += close.Length;
                            list.Add(new LTok(LTT.Comment, src.Substring(i, end - i)));
                            i = end; continue;
                        }
                    }
                    // Line comment
                    {
                        int j = i + 2;
                        while (j < n && src[j] != '\n') j++;
                        list.Add(new LTok(LTT.Comment, src.Substring(i, j - i)));
                        i = j; continue;
                    }
                }

                // Long string [=*[
                if (c == '[')
                {
                    int k = i + 1, eq = 0;
                    while (k < n && src[k] == '=') { eq++; k++; }
                    if (k < n && src[k] == '[')
                    {
                        k++;
                        string close = "]" + new string('=', eq) + "]";
                        int end = src.IndexOf(close, k);
                        if (end < 0) end = n - close.Length;
                        end += close.Length;
                        list.Add(new LTok(LTT.Str, src.Substring(i, end - i)));
                        i = end; continue;
                    }
                }

                // Quoted strings
                if (c == '"' || c == '\'')
                {
                    char q = c;
                    int j = i + 1;
                    while (j < n && src[j] != q)
                    {
                        if (src[j] == '\\') j++;
                        j++;
                    }
                    if (j < n) j++;
                    list.Add(new LTok(LTT.Str, src.Substring(i, j - i)));
                    i = j; continue;
                }

                // Numbers (including 0b and 0x)
                if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(src[i + 1])))
                {
                    int j = i;
                    if (c == '0' && i + 1 < n && ((src[i + 1] | 32) == 'x'))
                    {
                        j += 2;
                        while (j < n && IsHexDigit(src[j])) j++;
                        // PICO-8 hex fixed-point: 0xHHHH.hhhh
                        if (j < n && src[j] == '.' && j + 1 < n && IsHexDigit(src[j + 1]))
                        {
                            j++; // consume '.'
                            while (j < n && IsHexDigit(src[j])) j++;
                        }
                    }
                    else if (c == '0' && i + 1 < n && ((src[i + 1] | 32) == 'b'))
                    {
                        j += 2;
                        while (j < n && (src[j] == '0' || src[j] == '1')) j++;
                    }
                    else
                    {
                        while (j < n && (char.IsDigit(src[j]) || src[j] == '.')) j++;
                        if (j < n && ((src[j] | 32) == 'e'))
                        {
                            j++;
                            if (j < n && (src[j] == '+' || src[j] == '-')) j++;
                            while (j < n && char.IsDigit(src[j])) j++;
                        }
                    }
                    list.Add(new LTok(LTT.Num, src.Substring(i, j - i)));
                    i = j; continue;
                }

                // Identifiers / keywords
                if (c == '_' || char.IsLetter(c))
                {
                    int j = i + 1;
                    while (j < n && (src[j] == '_' || char.IsLetterOrDigit(src[j]))) j++;
                    list.Add(new LTok(LTT.Id, src.Substring(i, j - i)));
                    i = j; continue;
                }

                // 4-char operators (must check before 3-char/2-char)
                if (i + 3 < n)
                {
                    string s4 = src.Substring(i, 4);
                    switch (s4)
                    {
                        case ">>>=": case ">><=": case "<<>=":
                            list.Add(new LTok(LTT.Op, s4)); i += 4; continue;
                    }
                }

                // 3-char operators
                if (i + 2 < n)
                {
                    string s3 = src.Substring(i, 3);
                    switch (s3)
                    {
                        case ">>>": case "..=": case "^^=": case "<<=": case ">>=":
                        case ">><": case "<<>":
                            list.Add(new LTok(LTT.Op, s3)); i += 3; continue;
                    }
                }

                // 2-char operators
                if (i + 1 < n)
                {
                    string s2 = src.Substring(i, 2);
                    switch (s2)
                    {
                        case "^^": case ">>": case "<<": case "~=": case "!=":
                        case "<=": case ">=": case "==": case "..": case "::":
                        case "//":
                        // Compound assignment operators
                        case "+=": case "-=": case "*=": case "/=": case "%=":
                        case "^=": case "|=": case "&=": case "\\=":
                            list.Add(new LTok(LTT.Op, s2)); i += 2; continue;
                    }
                }

                // Single-char
                switch (c)
                {
                    case '+': case '-': case '*': case '/': case '%': case '^':
                    case '&': case '|': case '~': case '#': case '<': case '>':
                    case '=': case '\\': case '?':
                        list.Add(new LTok(LTT.Op, c.ToString())); i++; continue;
                    case '(': case ')': case '[': case ']': case '{': case '}':
                    case ',': case ';': case ':': case '.':
                        list.Add(new LTok(LTT.Punct, c.ToString())); i++; continue;
                }

                // Unknown character — pass through
                list.Add(new LTok(LTT.Op, c.ToString())); i++;
            }
            return list;
        }

        private static bool IsHexDigit(char c) =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        // All Lua keywords — l-value scan stops when it hits one at depth 0.
        private static readonly System.Collections.Generic.HashSet<string> _luaKw =
            new System.Collections.Generic.HashSet<string> {
                "and","break","do","else","elseif","end","false","for",
                "function","goto","if","in","local","nil","not","or",
                "repeat","return","then","true","until","while" };

        // Keywords that begin or close blocks/statements — RHS of compound assign must stop here
        private static readonly System.Collections.Generic.HashSet<string> _stmtKw =
            new System.Collections.Generic.HashSet<string> {
                "if","while","for","function","local",
                "then","do","else","elseif","end","until","return","break" };

        // ── L-value start scan (for compound assignment) ──────────────────────
        // Walks backward from `end` collecting only non-keyword identifiers,
        // '.', and balanced '[ ... ]' groups — stopping at any Lua keyword.
        private static int FindLValStart(System.Collections.Generic.List<LTok> t, int end)
        {
            int j = end, depth = 0;
            bool foundId = false; // true after accepting an identifier at depth 0
            while (j >= 0)
            {
                var tok = t[j];
                if (tok.T == LTT.Space)
                {
                    // Stop at any whitespace at depth 0: l-value components are never separated by spaces.
                    if (depth == 0) break;
                    j--; continue;
                }

                if (depth > 0)
                {
                    if (tok.T == LTT.Punct && tok.S == "[") { depth--; if (depth == 0) foundId = false; }
                    else if (tok.T == LTT.Punct && tok.S == "]") depth++;
                    j--; continue;
                }

                // depth == 0
                // If we already found an identifier and now see ']', that ']' belongs to a
                // PREVIOUS expression (e.g. "a[b]c[d]" should only yield "c[d]"). Stop.
                if (foundId && tok.T == LTT.Punct && tok.S == "]") break;

                if (tok.T == LTT.Id && !_luaKw.Contains(tok.S)) { foundId = true; j--; continue; }
                if (tok.T == LTT.Punct && tok.S == ".") { foundId = false; j--; continue; }
                if (tok.T == LTT.Punct && tok.S == "[") { foundId = false; j--; continue; }
                if (tok.T == LTT.Punct && tok.S == "]") { depth++; j--; continue; }

                break; // keyword, operator, number, string — stop
            }
            int start = j + 1;
            while (start <= end && start < t.Count && t[start].T == LTT.Space) start++;
            return start;
        }

        // ── Compound assignment rewrite ───────────────────────────────────────
        // a += b  →  a = a + b   (etc.)
        private static void RewriteCompoundAssign(System.Collections.Generic.List<LTok> toks)
        {
            var map = new System.Collections.Generic.Dictionary<string, string>
            {
                {"+=","+"},{"-=","-"},{"*=","*"},{"/=","/"},{"\\=","\\"},
                {"%=","%"},{"^=","^"},{"..=",".."},
                {"|=","|"},{"&=","&"},{"^^=","^^"},
                {"<<=","<<"},{">>>=",">>>"},
                {">>=",">>"},{"<<>=","<<>"},{">><=",">><"}
            };

            for (int i = 0; i < toks.Count; i++)
            {
                if (toks[i].T != LTT.Op) continue;
                if (!map.TryGetValue(toks[i].S, out string? binOp)) continue;

                int lhsEnd = i - 1;
                while (lhsEnd >= 0 && toks[lhsEnd].T == LTT.Space) lhsEnd--;
                if (lhsEnd < 0) continue;

                int lhsStart = FindLValStart(toks, lhsEnd);
                if (lhsStart > lhsEnd) continue;

                int rhsStart = i + 1;
                while (rhsStart < toks.Count && toks[rhsStart].T == LTT.Space) rhsStart++;
                if (rhsStart >= toks.Count) continue;

                // Collect RHS to end of statement.
                // Stops at: newline, ';', statement keywords, assignment operators,
                // or when a value-token at depth 0 is immediately followed by a new
                // identifier (that isn't 'and'/'or') — which signals a new statement.
                int rhsEnd = rhsStart - 1, rdepth = 0;
                bool lastWasValue = false;
                for (int k = rhsStart; k < toks.Count; k++)
                {
                    var tok = toks[k];
                    if (tok.T == LTT.Space)
                    {
                        if (tok.S.Contains('\n') && rdepth == 0) break;
                        continue;
                    }
                    if (rdepth == 0)
                    {
                        if (tok.T == LTT.Punct && tok.S == ";") break;
                        if (tok.T == LTT.Id && _stmtKw.Contains(tok.S)) break;
                        // Plain '=' or compound assignment op → new statement starting
                        if (tok.T == LTT.Op && tok.S == "=") break;
                        if (tok.T == LTT.Op && tok.S.Length >= 2
                            && tok.S[tok.S.Length - 1] == '='
                            && tok.S != "==" && tok.S != "!=" && tok.S != "~="
                            && tok.S != "<=" && tok.S != ">=") break;
                        // After a value, a new identifier (not 'and'/'or') starts a new statement
                        if (lastWasValue && tok.T == LTT.Id
                            && tok.S != "and" && tok.S != "or") break;
                    }
                    if (tok.T == LTT.Punct)
                    {
                        if (tok.S == "(" || tok.S == "[" || tok.S == "{") rdepth++;
                        else if (tok.S == ")" || tok.S == "]" || tok.S == "}")
                        {
                            if (rdepth == 0) break;
                            rdepth--;
                        }
                    }
                    lastWasValue = rdepth == 0 && (
                        tok.T == LTT.Num || tok.T == LTT.Str ||
                        (tok.T == LTT.Id && tok.S != "and" && tok.S != "or" && tok.S != "not") ||
                        (tok.T == LTT.Punct && (tok.S == ")" || tok.S == "]")));
                    rhsEnd = k;
                }
                if (rhsEnd < rhsStart) continue; // empty RHS — skip

                var lhs = toks.GetRange(lhsStart, lhsEnd - lhsStart + 1);
                var rhs = toks.GetRange(rhsStart, rhsEnd - rhsStart + 1);

                var repl = new System.Collections.Generic.List<LTok>(lhs.Count * 2 + rhs.Count + 6);
                repl.AddRange(lhs);
                repl.Add(new LTok(LTT.Op,    "="));
                repl.Add(new LTok(LTT.Space,  " "));
                repl.AddRange(lhs);
                repl.Add(new LTok(LTT.Space,  " "));
                repl.Add(new LTok(LTT.Op,     binOp));
                repl.Add(new LTok(LTT.Space,  " "));
                repl.AddRange(rhs);

                toks.RemoveRange(lhsStart, rhsEnd - lhsStart + 1);
                toks.InsertRange(lhsStart, repl);
                i = lhsStart + repl.Count - 1;
            }
        }

        // ── Short-form if  →  if (cond) then body [else body] end ────────────
        private static void RewriteShortIf(System.Collections.Generic.List<LTok> toks)
        {
            restart:
            for (int i = 0; i < toks.Count; i++)
            {
                if (toks[i].T != LTT.Id || toks[i].S != "if") continue;

                // Find opening '(' right after 'if'
                int j = i + 1;
                while (j < toks.Count && toks[j].T == LTT.Space && !toks[j].S.Contains('\n')) j++;
                if (j >= toks.Count || toks[j].T != LTT.Punct || toks[j].S != "(") continue;

                // Find matching ')'
                int condOpen = j, depth = 1;
                j++;
                while (j < toks.Count && depth > 0)
                {
                    if (toks[j].T == LTT.Punct)
                    {
                        if (toks[j].S == "(") depth++;
                        else if (toks[j].S == ")") depth--;
                    }
                    j++;
                }
                int condClose = j - 1; // index of ')'

                // Next non-space (same line) token after ')'
                int after = j;
                while (after < toks.Count && toks[after].T == LTT.Space
                                          && !toks[after].S.Contains('\n')) after++;

                if (after >= toks.Count) continue;
                // Regular form if followed by 'then' — leave alone
                if (toks[after].T == LTT.Id && toks[after].S == "then") continue;
                // Condition continues past ')' via operator — e.g. if (x)^2>1 then — leave alone
                if (toks[after].T == LTT.Op) continue;
                if (toks[after].T == LTT.Id
                    && (toks[after].S == "and" || toks[after].S == "or")) continue;
                // Newline only — leave alone (malformed but don't crash)
                if (toks[after].T == LTT.Space) continue;

                // Short-form: collect body to end of line.
                // Track nested block depth so we only stop at an 'end'/'until' that
                // belongs to the short-form if itself, not to nested function/if/for/while.
                int lineEnd = after, bodyDepth = 0;
                for (int k = after; k < toks.Count; k++)
                {
                    if (toks[k].T == LTT.Space && toks[k].S.Contains('\n')) break;
                    // Stop before a trailing comment so 'end' is inserted before it
                    if (toks[k].T == LTT.Comment) break;
                    if (toks[k].T == LTT.Id)
                    {
                        var ks = toks[k].S;
                        if (ks == "function" || ks == "if" || ks == "for" ||
                            ks == "while"    || ks == "do" || ks == "repeat")
                            bodyDepth++;
                        else if (ks == "end" || ks == "until")
                        {
                            if (bodyDepth == 0) break; // belongs to short-form if, stop here
                            bodyDepth--;
                        }
                    }
                    lineEnd = k;
                }

                // Split body at 'else' (depth 0)
                var thenToks = new System.Collections.Generic.List<LTok>();
                var elseToks = new System.Collections.Generic.List<LTok>();
                bool inElse = false;
                int bd = 0;
                for (int k = after; k <= lineEnd; k++)
                {
                    var tok = toks[k];
                    if (tok.T == LTT.Punct)
                    {
                        if (tok.S == "(" || tok.S == "[" || tok.S == "{") bd++;
                        else if (tok.S == ")" || tok.S == "]" || tok.S == "}") bd--;
                    }
                    if (!inElse && bd == 0 && tok.T == LTT.Id && tok.S == "else")
                    { inElse = true; continue; }
                    (inElse ? elseToks : thenToks).Add(tok);
                }

                // Trim leading/trailing spaces from each part
                static void Trim(System.Collections.Generic.List<LTok> lst) {
                    while (lst.Count > 0 && lst[0].T == LTT.Space) lst.RemoveAt(0);
                    while (lst.Count > 0 && lst[lst.Count-1].T == LTT.Space) lst.RemoveAt(lst.Count-1);
                }
                Trim(thenToks); Trim(elseToks);

                var condToks = toks.GetRange(condOpen, condClose - condOpen + 1);

                var repl = new System.Collections.Generic.List<LTok>();
                repl.Add(new LTok(LTT.Id,   "if"));
                repl.Add(new LTok(LTT.Space, " "));
                repl.AddRange(condToks);
                repl.Add(new LTok(LTT.Space, " "));
                repl.Add(new LTok(LTT.Id,   "then"));
                repl.Add(new LTok(LTT.Space, " "));
                repl.AddRange(thenToks);
                if (elseToks.Count > 0)
                {
                    repl.Add(new LTok(LTT.Space, " "));
                    repl.Add(new LTok(LTT.Id,   "else"));
                    repl.Add(new LTok(LTT.Space, " "));
                    repl.AddRange(elseToks);
                }
                repl.Add(new LTok(LTT.Space, " "));
                repl.Add(new LTok(LTT.Id,   "end"));
                repl.Add(new LTok(LTT.Space, " ")); // space after end to prevent 'endend' lexing

                toks.RemoveRange(i, lineEnd - i + 1);
                toks.InsertRange(i, repl);
                goto restart;
            }
        }

        // ── Short-form while  →  while (cond) do body end ────────────────────
        private static void RewriteShortWhile(System.Collections.Generic.List<LTok> toks)
        {
            restart:
            for (int i = 0; i < toks.Count; i++)
            {
                if (toks[i].T != LTT.Id || toks[i].S != "while") continue;

                int j = i + 1;
                while (j < toks.Count && toks[j].T == LTT.Space && !toks[j].S.Contains('\n')) j++;
                if (j >= toks.Count || toks[j].T != LTT.Punct || toks[j].S != "(") continue;

                int condOpen = j, depth = 1;
                j++;
                while (j < toks.Count && depth > 0)
                {
                    if (toks[j].T == LTT.Punct)
                    {
                        if (toks[j].S == "(") depth++;
                        else if (toks[j].S == ")") depth--;
                    }
                    j++;
                }
                int condClose = j - 1;

                int after = j;
                while (after < toks.Count && toks[after].T == LTT.Space
                                          && !toks[after].S.Contains('\n')) after++;
                if (after >= toks.Count) continue;
                if (toks[after].T == LTT.Id && toks[after].S == "do") continue;
                if (toks[after].T == LTT.Space) continue;
                // Condition continues past ')' via operator or and/or — leave alone
                if (toks[after].T == LTT.Op) continue;
                if (toks[after].T == LTT.Id && (toks[after].S == "and" || toks[after].S == "or")) continue;

                int lineEnd = after;
                for (int k = after; k < toks.Count; k++)
                {
                    if (toks[k].T == LTT.Space && toks[k].S.Contains('\n')) break;
                    if (toks[k].T == LTT.Comment) break; // don't include trailing comments
                    if (toks[k].T == LTT.Id && toks[k].S == "end") break;
                    lineEnd = k;
                }

                var bodyToks = toks.GetRange(after, lineEnd - after + 1);
                while (bodyToks.Count > 0 && bodyToks[0].T == LTT.Space) bodyToks.RemoveAt(0);
                while (bodyToks.Count > 0 && bodyToks[bodyToks.Count-1].T == LTT.Space) bodyToks.RemoveAt(bodyToks.Count-1);

                var condToks = toks.GetRange(condOpen, condClose - condOpen + 1);

                var repl = new System.Collections.Generic.List<LTok>();
                repl.Add(new LTok(LTT.Id,   "while"));
                repl.Add(new LTok(LTT.Space, " "));
                repl.AddRange(condToks);
                repl.Add(new LTok(LTT.Space, " "));
                repl.Add(new LTok(LTT.Id,   "do"));
                repl.Add(new LTok(LTT.Space, " "));
                repl.AddRange(bodyToks);
                repl.Add(new LTok(LTT.Space, " "));
                repl.Add(new LTok(LTT.Id,   "end"));
                repl.Add(new LTok(LTT.Space, " ")); // space after end to prevent 'endend' lexing

                toks.RemoveRange(i, lineEnd - i + 1);
                toks.InsertRange(i, repl);
                goto restart;
            }
        }

        // ── ? shorthand  →  print(…) ─────────────────────────────────────────
        private static void RewritePrintShorthand(System.Collections.Generic.List<LTok> toks)
        {
            for (int i = 0; i < toks.Count; i++)
            {
                bool isAsciiQ = toks[i].S == "?";
                bool isPico8Special = toks[i].S.Length == 1 && toks[i].S[0] > 127;
                if (!isAsciiQ && !isPico8Special) continue;

                // If ? (or a PICO-8 special char icon) appears in value position → replace with nil
                int prev = i - 1;
                while (prev >= 0 && toks[prev].T == LTT.Space) prev--;
                // Check if between :: label markers → replace with a valid identifier
                bool inLabel = prev >= 0 && toks[prev].T == LTT.Op && toks[prev].S == "::";
                if (inLabel) { toks[i] = new LTok(LTT.Id, "_p8icon"); continue; }
                // Check if after 'goto' keyword → replace label target with valid identifier
                bool afterGoto = prev >= 0 && toks[prev].T == LTT.Id && toks[prev].S == "goto";
                if (afterGoto) { toks[i] = new LTok(LTT.Id, "_p8icon"); continue; }
                bool inValuePos = prev >= 0 && (
                    (toks[prev].T == LTT.Punct && (toks[prev].S == "(" || toks[prev].S == ",")) ||
                    (toks[prev].T == LTT.Op && toks[prev].S == "="));
                if (inValuePos)
                {
                    toks[i] = new LTok(LTT.Id, "nil");
                    continue;
                }
                // PICO-8 special chars at statement level (not ?) → remove silently
                if (isPico8Special) { toks[i] = new LTok(LTT.Space, ""); continue; }
                // ASCII ? at statement level falls through to print(...) handling below

                // Collect arguments to end of line
                int argStart = i + 1;
                while (argStart < toks.Count && toks[argStart].T == LTT.Space
                                             && !toks[argStart].S.Contains('\n')) argStart++;

                int argEnd = argStart;
                int depth = 0;
                for (int k = argStart; k < toks.Count; k++)
                {
                    if (toks[k].T == LTT.Space && toks[k].S.Contains('\n') && depth == 0) break;
                    if (toks[k].T == LTT.Punct)
                    {
                        if (toks[k].S == "(" || toks[k].S == "[" || toks[k].S == "{") depth++;
                        else if (toks[k].S == ")" || toks[k].S == "]" || toks[k].S == "}")
                        { if (depth == 0) break; depth--; }
                    }
                    argEnd = k;
                }

                var args = toks.GetRange(argStart, Math.Max(0, argEnd - argStart + 1));
                while (args.Count > 0 && args[0].T == LTT.Space) args.RemoveAt(0);
                while (args.Count > 0 && args[args.Count-1].T == LTT.Space) args.RemoveAt(args.Count-1);

                var repl = new System.Collections.Generic.List<LTok>();
                repl.Add(new LTok(LTT.Id,    "print"));
                repl.Add(new LTok(LTT.Punct,  "("));
                repl.AddRange(args);
                repl.Add(new LTok(LTT.Punct,  ")"));

                int removeLen = (args.Count == 0 ? 0 : argEnd - i) + 1;
                toks.RemoveRange(i, Math.Max(1, removeLen));
                toks.InsertRange(i, repl);
                i += repl.Count - 1;
            }
        }

        // ── LHS scan (goes backward from end, returns start index) ────────────
        private static int LhsScan(System.Collections.Generic.List<LTok> t, int end, string op)
        {
            int depth = 0, j = end;
            while (j >= 0)
            {
                var tok = t[j];
                if (tok.T == LTT.Space) { j--; continue; }

                if (tok.T == LTT.Punct && (tok.S == ")" || tok.S == "]" || tok.S == "}"))
                { depth++; j--; continue; }

                if (tok.T == LTT.Punct && (tok.S == "(" || tok.S == "[" || tok.S == "{"))
                {
                    if (depth == 0) return SkipSpcFwd(t, j + 1, end);
                    depth--; j--; continue;
                }

                if (depth == 0)
                {
                    if (tok.T == LTT.Punct && (tok.S == "," || tok.S == ";"))
                        return SkipSpcFwd(t, j + 1, end);
                    if (tok.T == LTT.Op && tok.S == "=")
                        return SkipSpcFwd(t, j + 1, end);
                    if (tok.T == LTT.Id && _lkBack.Contains(tok.S))
                        return SkipSpcFwd(t, j + 1, end);
                    if (tok.T == LTT.Op && (_lopStop.Contains(tok.S) || tok.S == op))
                        return SkipSpcFwd(t, j + 1, end);
                }
                j--;
            }
            return SkipSpcFwd(t, 0, end);
        }

        private static int SkipSpcFwd(System.Collections.Generic.List<LTok> t, int from, int max)
        {
            while (from <= max && from < t.Count && t[from].T == LTT.Space) from++;
            return from;
        }

        // ── RHS scan (goes forward from begin, returns end index inclusive) ───
        private static int RhsScan(System.Collections.Generic.List<LTok> t, int begin, string op, bool unary)
        {
            int depth = 0, j = begin, end = begin;
            bool hasAny = false;
            while (j < t.Count)
            {
                var tok = t[j];
                if (tok.T == LTT.Space) { j++; continue; }

                if (tok.T == LTT.Punct && (tok.S == "(" || tok.S == "[" || tok.S == "{"))
                { depth++; end = j; hasAny = true; j++; continue; }

                if (tok.T == LTT.Punct && (tok.S == ")" || tok.S == "]" || tok.S == "}"))
                {
                    if (depth == 0) break;
                    depth--; end = j; j++; continue;
                }

                if (depth == 0)
                {
                    if (tok.T == LTT.Punct && (tok.S == "," || tok.S == ";")) break;
                    if (tok.T == LTT.Id   && _lkFwd.Contains(tok.S)) break;
                    if (tok.T == LTT.Op)
                    {
                        bool stop = _lopStop.Contains(tok.S) || tok.S == op;
                        // For unary RHS: also stop at arithmetic binary ops once we have something
                        if (!stop && unary && hasAny)
                            stop = tok.S == "+" || tok.S == "-" || tok.S == "*" || tok.S == "/" ||
                                   tok.S == "%" || tok.S == "^" || tok.S == "\\" || tok.S == "//";
                        if (stop) break;
                    }
                }

                end = j; hasAny = true; j++;
            }
            return end;
        }

        // ── Binary operator rewrite ───────────────────────────────────────────
        private static void RewriteBin(System.Collections.Generic.List<LTok> toks, string op, string fn,
                                       bool skipIfUnary = false)
        {
            restart:
            for (int i = 0; i < toks.Count; i++)
            {
                if (toks[i].T != LTT.Op || toks[i].S != op) continue;
                // For operators that can also be unary (e.g. '~'), skip if in unary position
                if (skipIfUnary && IsUnaryPos(toks, i)) continue;

                // Find LHS end (last non-space token before op)
                int lhsEnd = i - 1;
                while (lhsEnd >= 0 && toks[lhsEnd].T == LTT.Space) lhsEnd--;
                if (lhsEnd < 0) continue;

                int lhsStart = LhsScan(toks, lhsEnd, op);

                // Find RHS start (first non-space token after op)
                int rhsStart = i + 1;
                while (rhsStart < toks.Count && toks[rhsStart].T == LTT.Space) rhsStart++;
                if (rhsStart >= toks.Count) continue;

                int rhsEnd = RhsScan(toks, rhsStart, op, false);

                var lhsPart = toks.GetRange(lhsStart, lhsEnd - lhsStart + 1);
                var rhsPart = toks.GetRange(rhsStart, rhsEnd - rhsStart + 1);

                var repl = new System.Collections.Generic.List<LTok>(lhsPart.Count + rhsPart.Count + 5);
                repl.Add(new LTok(LTT.Id,    fn));
                repl.Add(new LTok(LTT.Punct, "("));
                repl.AddRange(lhsPart);
                repl.Add(new LTok(LTT.Punct, ","));
                repl.Add(new LTok(LTT.Space, " "));
                repl.AddRange(rhsPart);
                repl.Add(new LTok(LTT.Punct, ")"));

                toks.RemoveRange(lhsStart, rhsEnd - lhsStart + 1);
                toks.InsertRange(lhsStart, repl);
                goto restart; // restart scan since indices changed
            }
        }

        // ── Unary ~ → bnot(...) ───────────────────────────────────────────────
        private static bool IsUnaryPos(System.Collections.Generic.List<LTok> t, int i)
        {
            int j = i - 1;
            while (j >= 0 && t[j].T == LTT.Space) j--;
            if (j < 0) return true;
            var p = t[j];
            if (p.T == LTT.Op) return true;
            if (p.T == LTT.Punct) return p.S == "(" || p.S == "[" || p.S == "{" || p.S == "," || p.S == ";";
            if (p.T == LTT.Id)   return _lkBack.Contains(p.S);
            return false;
        }

        private static void RewriteUnary(System.Collections.Generic.List<LTok> toks, string op, string fn)
        {
            for (int i = 0; i < toks.Count; i++)
            {
                if (toks[i].T != LTT.Op || toks[i].S != op) continue;
                if (!IsUnaryPos(toks, i)) continue;

                int rhsStart = i + 1;
                while (rhsStart < toks.Count && toks[rhsStart].T == LTT.Space) rhsStart++;
                if (rhsStart >= toks.Count) continue;

                int rhsEnd = RhsScan(toks, rhsStart, op, true);

                var rhsPart = toks.GetRange(rhsStart, rhsEnd - rhsStart + 1);

                var repl = new System.Collections.Generic.List<LTok>(rhsPart.Count + 3);
                repl.Add(new LTok(LTT.Id,    fn));
                repl.Add(new LTok(LTT.Punct, "("));
                repl.AddRange(rhsPart);
                repl.Add(new LTok(LTT.Punct, ")"));

                toks.RemoveRange(i, rhsEnd - i + 1);
                toks.InsertRange(i, repl);
                i += repl.Count - 1; // skip past replacement, then i++ in loop
            }
        }

        // ── Goto scoping fix ─────────────────────────────────────────────────────
        // PICO-8's Lua allows goto to jump over local variable declarations, but
        // MoonSharp's Lua 5.2 does not.  For each `goto LABEL` that would jump over
        // a `local VAR` declaration, we hoist the `local VAR` declaration to just
        // before the goto statement.
        //
        // For "local VAR" (no init):  hoist "local VAR", erase the original.
        // For "local VAR = EXPR":     hoist "local VAR", keep "VAR = EXPR" in place.
        //
        // Only hoists locals that are at the same block depth as the ::LABEL:: target
        // (i.e., depth 0 relative to the label's enclosing block).  Locals inside any
        // nested block between goto and label are left alone.
        private static string RewriteGotoScoping(string src)
        {
            if (!src.Contains("goto ")) return src;
            var toks = LuaLex(src);
            bool changed = false;

            for (int gi = 0; gi < toks.Count - 1; gi++)
            {
                if (toks[gi].T != LTT.Id || toks[gi].S != "goto") continue;

                // Get the label name
                int ni = gi + 1;
                while (ni < toks.Count && toks[ni].T == LTT.Space) ni++;
                if (ni >= toks.Count || toks[ni].T != LTT.Id) continue;
                string labelName = toks[ni].S;

                // Find ::labelName:: scanning forward.
                // Track depth so we only collect locals at depth-0 relative to the
                // scope that contains the label (we count depth from the goto outward).
                // We start counting at the depth AFTER the goto's own nesting:
                // — each 'end'/'until' we encounter at depth>0 decrements depth
                // — each 'end'/'until' at depth==0 means we exited the block containing goto
                //   and moved to an outer scope (we allow collecting locals here).
                //   Once depth goes negative, we're at the scope of the label.
                // Collect 'local' tokens only when depth <= 0 (outer or same scope as label).

                int depth = 0;
                var localsToHoist = new System.Collections.Generic.List<(int localIdx, int varIdx, int eqIdx)>();
                int labelTokenIdx = -1;

                for (int si = ni + 1; si < toks.Count; si++)
                {
                    var tok = toks[si];
                    if (tok.T == LTT.Space || tok.T == LTT.Comment) continue;

                    if (tok.T == LTT.Id)
                    {
                        switch (tok.S)
                        {
                            case "if": case "for": case "while": case "do":
                            case "function": case "repeat":
                                depth++; break;
                            case "end":
                                if (depth > 0) depth--;
                                else depth--; // going negative means exiting the block containing goto
                                break;
                            case "until":
                                if (depth > 0) depth--;
                                else depth--;
                                break;
                            case "local":
                                // Only hoist if we're at or outside the goto's scope (depth ≤ 0)
                                // AND not inside a nested function definition
                                if (depth <= 0)
                                {
                                    // get var name
                                    int vni2 = si + 1;
                                    while (vni2 < toks.Count && toks[vni2].T == LTT.Space) vni2++;
                                    if (vni2 < toks.Count && toks[vni2].T == LTT.Id)
                                    {
                                        // check for '=' or ',' after variable (multi-var local)
                                        int eqCheck = vni2 + 1;
                                        while (eqCheck < toks.Count && toks[eqCheck].T == LTT.Space) eqCheck++;
                                        int eqPos = -1;
                                        bool isMultiVar = false;
                                        if (eqCheck < toks.Count && toks[eqCheck].T == LTT.Op && toks[eqCheck].S == "=")
                                            eqPos = eqCheck;
                                        else if (eqCheck < toks.Count && toks[eqCheck].T == LTT.Punct && toks[eqCheck].S == ",")
                                        {
                                            eqPos = eqCheck; // treat multi-var as having initializer → erase only 'local'
                                            isMultiVar = true;
                                        }
                                        localsToHoist.Add((si, vni2, eqPos));
                                        // For multi-var locals, also hoist subsequent variables
                                        if (isMultiVar)
                                        {
                                            int scan = eqCheck; // starts at ','
                                            while (scan < toks.Count)
                                            {
                                                scan++;
                                                while (scan < toks.Count && toks[scan].T == LTT.Space) scan++;
                                                if (scan >= toks.Count || toks[scan].T != LTT.Id) break;
                                                int extraVar = scan;
                                                scan++;
                                                while (scan < toks.Count && toks[scan].T == LTT.Space) scan++;
                                                if (scan < toks.Count && toks[scan].T == LTT.Punct && toks[scan].S == ",")
                                                    localsToHoist.Add((-1, extraVar, scan)); // sentinel -1 = no local to erase
                                                else if (scan < toks.Count && toks[scan].T == LTT.Op && toks[scan].S == "=")
                                                    { localsToHoist.Add((-1, extraVar, scan)); break; }
                                                else
                                                    { localsToHoist.Add((-1, extraVar, -1)); break; }
                                            }
                                        }
                                    }
                                }
                                break;
                        }
                    }
                    else if (tok.T == LTT.Op && tok.S == "::")
                    {
                        // Check for ::labelName::
                        int ni2 = si + 1;
                        while (ni2 < toks.Count && toks[ni2].T == LTT.Space) ni2++;
                        if (ni2 < toks.Count && toks[ni2].T == LTT.Id && toks[ni2].S == labelName)
                        {
                            int ci = ni2 + 1;
                            while (ci < toks.Count && toks[ci].T == LTT.Space) ci++;
                            if (ci < toks.Count && toks[ci].T == LTT.Op && toks[ci].S == "::")
                            {
                                labelTokenIdx = si;
                                break;
                            }
                        }
                    }
                }

                if (labelTokenIdx < 0 || localsToHoist.Count == 0) continue;
                changed = true;

                // Build hoisted declarations and fix original locations.
                var hoistSb = new System.Text.StringBuilder();
                foreach (var (localIdx, varIdx, eqIdx) in localsToHoist)
                {
                    string varName = toks[varIdx].S;
                    hoistSb.Append("local ").Append(varName).Append(' ');

                    if (localIdx < 0) continue; // extra variable in multi-var list, no local token to erase

                    if (eqIdx < 0)
                    {
                        // "local VAR" — erase local + var (keep any surrounding whitespace)
                        toks[localIdx] = new LTok(LTT.Space, "");
                        toks[varIdx]   = new LTok(LTT.Space, "");
                    }
                    else
                    {
                        // "local VAR = EXPR" or "local VAR, ..." — erase just 'local' (keep "VAR = EXPR")
                        toks[localIdx] = new LTok(LTT.Space, "");
                    }
                }

                // Insert hoisted text immediately before the 'goto' token
                toks.Insert(gi, new LTok(LTT.Space, hoistSb.ToString()));
                gi++; // skip the inserted token so we don't re-examine it
            }

            if (!changed) return src;
            var sb2 = new System.Text.StringBuilder(src.Length + 64);
            foreach (var t in toks) sb2.Append(t.S);
            return sb2.ToString();
        }

        // ── Entry point ───────────────────────────────────────────────────────
        // Rewrite `for VAR in all(EXPR) do ... end` to a numeric for loop to work
        // around a MoonSharp bug where goto inside numeric-for corrupts the generic
        // for-in loop iterator.
        //
        // Wraps the transformed loop in "do...end" so the local __all_tN variable is
        // scoped inside that block. This prevents Lua from complaining when a goto
        // (e.g. "goto reevaluate") jumps forward past the transformed loop's local.
        private static string RewriteForInAll(string src)
        {
            if (!src.Contains(" in all(") && !src.Contains(" in all (")) return src;
            var sb = new System.Text.StringBuilder(src.Length + 128);
            int counter = 0;
            int i = 0;
            while (i < src.Length)
            {
                // Look for pattern: "for" whitespace* NAME whitespace* "in" whitespace* "all" whitespace* "(" EXPR ")" whitespace* "do"
                int patStart = src.IndexOf("in all(", i, StringComparison.Ordinal);
                if (patStart < 0)
                {
                    sb.Append(src, i, src.Length - i);
                    break;
                }
                // Scan backward to find "for"
                int forStart = patStart - 1;
                while (forStart >= i && (src[forStart] == ' ' || src[forStart] == '\t' || src[forStart] == '\r' || src[forStart] == '\n')) forStart--;
                // forStart should be at the end of the var name
                int varEnd = forStart;
                while (forStart >= i && (char.IsLetterOrDigit(src[forStart]) || src[forStart] == '_')) forStart--;
                string varName = src.Substring(forStart + 1, varEnd - forStart);
                // Check for "for " before the var
                int forKw = forStart;
                while (forKw >= i && (src[forKw] == ' ' || src[forKw] == '\t')) forKw--;
                if (forKw < 3 || src.Substring(forKw - 2, 3) != "for" || varName.Length == 0)
                {
                    // Not the pattern we want, skip past this 'in all('
                    sb.Append(src, i, patStart - i + 7);
                    i = patStart + 7;
                    continue;
                }
                int forKeywordStart = forKw - 2;
                // Find matching ')' after 'all('
                int exprStart = patStart + 7; // after "in all("
                int depth = 1;
                int j = exprStart;
                while (j < src.Length && depth > 0)
                {
                    if (src[j] == '(') depth++;
                    else if (src[j] == ')') depth--;
                    if (depth > 0) j++;
                    else break;
                }
                if (j >= src.Length) { sb.Append(src, i, src.Length - i); break; }
                string expr = src.Substring(exprStart, j - exprStart);
                int afterParen = j + 1; // after ')'
                // Consume optional whitespace and "do"
                int k = afterParen;
                while (k < src.Length && (src[k] == ' ' || src[k] == '\t')) k++;
                if (k + 1 < src.Length && src[k] == 'd' && src[k+1] == 'o')
                {
                    // Full match! Emit everything before 'for'
                    sb.Append(src, i, forKeywordStart - i);
                    string t = $"__all_t{counter}";
                    string idx = $"__all_i{counter}";
                    counter++;
                    // Emit: local T=(EXPR) or {} for IDX=1,#T do local VAR=T[IDX]
                    // (RewriteGotoScoping handles any goto scoping issues)
                    sb.Append($"local {t}=({expr}) or {{}} for {idx}=1,#{t} do local {varName}={t}[{idx}]");
                    i = k + 2; // skip "do"
                }
                else
                {
                    // No "do" found — emit as-is
                    sb.Append(src, i, afterParen - i);
                    i = afterParen;
                }
            }
            return sb.ToString();
        }

        // Scans from bodyStart (just after the "do" keyword of a for/if/while/do/function block)
        // and returns the position immediately AFTER the matching "end" (or "until" for repeat).
        // depth=1 means we're inside one block.
        //
        // Depth counting rules (Lua block semantics):
        //   "if"/"function"/"repeat" → depth++   (these have exactly one matching end/until)
        //   "do" → depth++                       (standalone do, or the 'do' part of for/while headers)
        //   "for"/"while" → NO depth change      (their 'do' at the end of the header handles it)
        //   "end" → depth--  (if → 0, return)
        //   "until" → depth-- (closes repeat)
        private static int FindForMatchingEnd(string src, int bodyStart)
        {
            int depth = 1;
            int i = bodyStart;
            int n = src.Length;
            while (i < n && depth > 0)
            {
                char c = src[i];

                // Skip line comments: --
                if (c == '-' && i + 1 < n && src[i + 1] == '-')
                {
                    i += 2;
                    // Check for long comment --[[ ... ]]
                    if (i < n && src[i] == '[')
                    {
                        int level = 0;
                        int j = i + 1;
                        while (j < n && src[j] == '=') { level++; j++; }
                        if (j < n && src[j] == '[')
                        {
                            string close = "]" + new string('=', level) + "]";
                            int ci = src.IndexOf(close, j + 1, StringComparison.Ordinal);
                            i = ci >= 0 ? ci + close.Length : n;
                            continue;
                        }
                    }
                    while (i < n && src[i] != '\n') i++;
                    continue;
                }

                // Skip long strings: [[ ... ]] or [=[ ... ]=]
                if (c == '[' && i + 1 < n && (src[i + 1] == '[' || src[i + 1] == '='))
                {
                    int level = 0;
                    int j = i + 1;
                    while (j < n && src[j] == '=') { level++; j++; }
                    if (j < n && src[j] == '[')
                    {
                        string close = "]" + new string('=', level) + "]";
                        int ci = src.IndexOf(close, j + 1, StringComparison.Ordinal);
                        i = ci >= 0 ? ci + close.Length : n;
                        continue;
                    }
                }

                // Skip single-quoted strings
                if (c == '\'' || c == '"')
                {
                    char q = c;
                    i++;
                    while (i < n && src[i] != q)
                    {
                        if (src[i] == '\\') i++; // skip escape
                        i++;
                    }
                    if (i < n) i++; // skip closing quote
                    continue;
                }

                // Keyword scan
                if (char.IsLetter(c) || c == '_')
                {
                    int j = i + 1;
                    while (j < n && (char.IsLetterOrDigit(src[j]) || src[j] == '_')) j++;
                    string word = src.Substring(i, j - i);
                    switch (word)
                    {
                        case "if":
                        case "do":        // standalone 'do', or the trailing 'do' of for/while headers
                        case "function":
                        case "repeat":
                            depth++;
                            break;
                        // "for" and "while": their depth is handled by the 'do' at the end of their header.
                        // Do NOT increment here to avoid double-counting.
                        case "end":
                            depth--;
                            if (depth == 0) return j; // return position AFTER 'end'
                            break;
                        case "until":
                            // closes a 'repeat' block
                            depth--;
                            break;
                    }
                    i = j;
                    continue;
                }

                i++;
            }
            return i;
        }

        // ── Extract 'end' from line-end comments when it closes an if-then ───────
        // Handles PICO-8 pattern: if cond then body -- label end
        // where the author placed 'end' after a comment marker by mistake.
        private static void RewriteEndInComment(System.Collections.Generic.List<LTok> toks)
        {
            for (int i = 0; i < toks.Count; i++)
            {
                if (toks[i].T != LTT.Comment) continue;
                string c = toks[i].S;
                // Only single-line comments (--...) that end with the word 'end'
                if (!c.StartsWith("--") || c.StartsWith("--[[")) continue;
                var m = System.Text.RegularExpressions.Regex.Match(c, @"\bend\s*$");
                if (!m.Success) continue;

                // Check that there's a 'then' on the same line before this comment
                // (meaning we're inside a one-liner if...then...end)
                bool foundThen = false;
                int depth = 0;
                for (int j = i - 1; j >= 0; j--)
                {
                    if (toks[j].T == LTT.Space && toks[j].S.Contains('\n')) break;
                    if (toks[j].T == LTT.Id && toks[j].S == "then" && depth == 0) { foundThen = true; break; }
                    if (toks[j].T == LTT.Id && (toks[j].S == "end" || toks[j].S == "until")) depth++;
                    if (toks[j].T == LTT.Id && (toks[j].S == "do" || toks[j].S == "function" ||
                        toks[j].S == "if" || toks[j].S == "for" || toks[j].S == "while")) { if (depth > 0) depth--; }
                }
                if (!foundThen) continue;

                // Trim 'end' from the comment and add a real 'end' token after it
                string trimmed = c.Substring(0, m.Index).TrimEnd();
                toks[i] = new LTok(LTT.Comment, trimmed.Length > 2 ? trimmed : "--");
                toks.Insert(i + 1, new LTok(LTT.Id, "end"));
                i++; // skip the newly inserted token
            }
        }

        private static string PreprocessLua(string src)
        {
            if (string.IsNullOrEmpty(src)) return src;

            // Convert C-style // line comments (at start of line) to Lua -- comments
            // PICO-8 sometimes uses // as a line comment (disabled code)
            src = System.Text.RegularExpressions.Regex.Replace(src, @"(?m)^([ \t]*)//", "$1--");

            var toks = LuaLex(src);

            // 1. Structural rewrites (must come first, before operator expansion)
            RewriteEndInComment(toks);    // if cond then body --label end  →  extract end from comment
            RewritePrintShorthand(toks);  // ? expr  →  print(expr)
            RewriteCompoundAssign(toks);  // a += b  →  a = a + b
            RewriteShortIf(toks);         // if (c) s      →  if (c) then s end
            RewriteShortWhile(toks);      // while (c) s   →  while (c) do s end

            // 2. Binary operator rewrites, lowest PICO-8 precedence first
            RewriteBin(toks, "|",    "bor");
            RewriteBin(toks, "&",    "band");
            RewriteBin(toks, "^^",   "bxor");
            RewriteBin(toks, ">>>",  "lshr");
            RewriteBin(toks, ">>",   "shr");
            RewriteBin(toks, "<<",   "shl");
            RewriteBin(toks, ">><",  "rotr");
            RewriteBin(toks, "<<>",  "rotl");
            RewriteBin(toks, "\\",   "__idiv");
            RewriteBin(toks, "//",   "__idiv");

            // 3. Binary ~  →  bxor(...) (must precede unary ~ rewrite)
            RewriteBin(toks, "~", "bxor", skipIfUnary: true);

            // 4. Unary ~  →  bnot(...)
            RewriteUnary(toks, "~", "bnot");

            // 4. Token-level text transforms
            var sb = new System.Text.StringBuilder(src.Length + 32);
            foreach (var tok in toks)
            {
                if (tok.T == LTT.Num && tok.S.Length > 2 &&
                    tok.S[0] == '0' && (tok.S[1] == 'b' || tok.S[1] == 'B'))
                {
                    // 0b binary literal → decimal
                    sb.Append(Convert.ToInt64(tok.S.Substring(2), 2));
                }
                else if (tok.T == LTT.Num && tok.S.Length > 2 &&
                         tok.S[0] == '0' && (tok.S[1] == 'x' || tok.S[1] == 'X') &&
                         tok.S.Contains('.'))
                {
                    // PICO-8 hex fixed-point 0xHHHH.hhhh → decimal float
                    var dot = tok.S.IndexOf('.');
                    var intHex  = tok.S.Substring(2, dot - 2);
                    var fracHex = tok.S.Substring(dot + 1);
                    long intPart  = intHex.Length  > 0 ? Convert.ToInt64(intHex,  16) : 0;
                    long fracRaw  = fracHex.Length > 0 ? Convert.ToInt64(fracHex, 16) : 0;
                    double fracPart = fracRaw / Math.Pow(16, fracHex.Length);
                    sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                              $"{intPart + fracPart:R}");
                }
                else if (tok.T == LTT.Op && tok.S == "!=")
                    sb.Append("~=");  // PICO-8 !=  →  Lua ~=
                else
                    sb.Append(tok.S);
            }
            return sb.ToString();
        }

        // ── String helpers ───────────────────────────────────────────────────

        private string Tostr(DynValue v, DynValue hex)
        {
            if (v.Type == DataType.Boolean) return v.Boolean ? "true" : "false";
            if (v.Type == DataType.Number)
            {
                bool h = !hex.IsNil() && (hex.CastToNumber()??0)!=0;
                if (h) return $"0x{(long)v.Number:x4}";
                return FormatNum(v.Number);
            }
            return v.ToString() ?? "";
        }

        private DynValue Tonum(DynValue v)
        {
            if (v.Type == DataType.Number) return v;
            string s = v.ToString() ?? "";
            // hex literals
            if (s.StartsWith("0x",StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(s.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out long hv))
                    return DynValue.NewNumber(hv);
            }
            if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double d))
                return DynValue.NewNumber(d);
            return DynValue.Nil;
        }

        private static string Pico8Sub(string s, double from, double to)
        {
            int f=(int)from-1, t=(int)to;
            if (f<0)f=0; if (t>s.Length)t=s.Length;
            if (f>=t) return "";
            return s.Substring(f, t-f);
        }

        private double Ord(DynValue s, DynValue i)
        {
            string str = s.Type == DataType.String ? s.String : (s.ToString() ?? "");
            int idx = i.IsNil() ? 0 : (int)(i.CastToNumber()??1)-1;
            if (idx<0||idx>=str.Length) return 0;
            return str[idx];
        }

        // split(s [, sep [, convert]])
        // sep: separator string (default ","), or "" / nil for per-character split
        // convert: if true, convert numeric strings to numbers
        private DynValue Split(DynValue strV, DynValue sepV, DynValue cvV)
        {
            string s   = strV.Type == DataType.String ? strV.String : (strV.ToString() ?? "");
            string sep = sepV.IsNil() ? "," : (sepV.Type == DataType.String ? sepV.String : ",");
            bool cv    = !cvV.IsNil() && (cvV.CastToNumber() ?? 0) != 0;

            var tbl = new Table(_lua);
            if (sep.Length == 0)
            {
                // Per-character split
                for (int i = 0; i < s.Length; i++)
                {
                    string ch = s[i].ToString();
                    tbl.Append(cv && double.TryParse(ch, out double d)
                        ? DynValue.NewNumber(d)
                        : DynValue.NewString(ch));
                }
            }
            else
            {
                var parts = s.Split(sep, StringSplitOptions.None);
                foreach (var part in parts)
                {
                    tbl.Append(cv && double.TryParse(part,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double d)
                        ? DynValue.NewNumber(d)
                        : DynValue.NewString(part));
                }
            }
            return DynValue.NewTable(tbl);
        }

        // strformat(fmt, ...) — thin wrapper; PICO-8 uses string.format syntax
        private string StrFormat(DynValue fmt, DynValue a, DynValue b, DynValue c, DynValue d)
        {
            // Delegate to Lua's string.format via the VM
            try
            {
                string fmtStr = fmt.Type == DataType.String ? fmt.String : (fmt.ToString() ?? "");
                var args = new List<DynValue> { fmt };
                if (!a.IsNil()) args.Add(a);
                if (!b.IsNil()) args.Add(b);
                if (!c.IsNil()) args.Add(c);
                if (!d.IsNil()) args.Add(d);
                var fn = _lua.Globals.Get("string").Table.Get("format");
                var result = _lua.Call(fn, args.ToArray());
                return result.Type == DataType.String ? result.String : "";
            }
            catch { return ""; }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private byte ColorArg(DynValue v)
        {
            if (v.IsNil()) return _drawColor;
            byte col = (byte)((int)(v.CastToNumber()??_drawColor) & 0xF);
            _drawColor = col;
            return col;
        }

        private byte ColorDyn(DynValue v)
        {
            if (v.IsNil()) return _drawColor;
            byte col = (byte)((int)(v.CastToNumber()??_drawColor) & 0xF);
            if (_fillpSecondaryOpaque)
                _fillpSecondaryColor = (byte)(((int)(v.CastToNumber()??0) >> 4) & 0xF);
            _drawColor = col;
            return col;
        }

        private static double MidFn(double a, double b, double c)
        {
            double[] v={a,b,c}; Array.Sort(v); return v[1];
        }

        private static string FormatNum(double n)
        {
            if (n == Math.Floor(n) && Math.Abs(n) < 1e15) return ((long)n).ToString();
            return n.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        }

        // Convert DynValue to long for bitwise ops; false→0, true→1, nil→0.
        private static long DynToLong(DynValue v)
        {
            if (v.Type == DataType.Boolean) return v.Boolean ? 1L : 0L;
            if (v.Type == DataType.Nil || v.Type == DataType.Void) return 0L;
            return (long)(v.CastToNumber() ?? 0.0);
        }

        private static string DynToString(DynValue v) => v.Type switch
        {
            DataType.String  => v.String,
            DataType.Number  => FormatNum(v.Number),
            DataType.Boolean => v.Boolean ? "true" : "false",
            DataType.Nil     => "",
            _                => v.ToString() ?? ""
        };
    }
}

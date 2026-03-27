namespace PspEmu;

/// <summary>
/// HLE module registry: maps PSP firmware NIDs to C# implementations.
/// Each PSP function is identified by a NID (Name ID hash).
/// Import stubs in the loaded ELF are patched with SYSCALL instructions
/// that encode the syscall code, which we resolve here.
/// </summary>
sealed class HleModules
{
    readonly HleKernel _kernel;
    readonly PspBus _bus;
    readonly PspDisplay _display;
    readonly PspAudio _audio;
    readonly PspCtrl _ctrl;
    readonly HleIo _io;
    readonly PspGe _ge;

    // Syscall code → handler
    readonly Dictionary<uint, Action<Allegrex>> _syscalls = new();
    readonly Dictionary<uint, string> _syscallNames = new();

    // NID → syscall code (for import resolution)
    readonly Dictionary<uint, uint> _nidToCode = new();

    uint _nextSyscallCode;

    public HleModules(HleKernel kernel, PspBus bus, PspDisplay display, PspAudio audio, PspCtrl ctrl, HleIo io, PspGe ge)
    {
        _kernel = kernel;
        _bus = bus;
        _display = display;
        _audio = audio;
        _ctrl = ctrl;
        _io = io;
        _ge = ge;
        RegisterAll();
    }

    public bool TryDispatch(uint code, Allegrex cpu)
    {
        if (_syscalls.TryGetValue(code, out var handler))
        {
            handler(cpu);
            return true;
        }
        return false;
    }

    /// <summary>Look up the syscall code for a given NID. Returns 0xFFFFFFFF if not found.</summary>
    public uint GetSyscallForNid(uint nid)
    {
        return _nidToCode.TryGetValue(nid, out uint code) ? code : 0xFFFFFFFF;
    }

    public string GetSyscallName(uint code) =>
        _syscallNames.TryGetValue(code, out var name) ? name : $"unknown_{code:X}";

    /// <summary>Register an HLE function by NID.</summary>
    uint Reg(uint nid, string name, Action<Allegrex> handler)
    {
        uint code = _nextSyscallCode++;
        _syscalls[code] = handler;
        _syscallNames[code] = name;
        _nidToCode[nid] = code;
        return code;
    }

    // MIPS o32 calling convention helpers
    static uint A0(Allegrex c) => c.Gpr[4];
    static uint A1(Allegrex c) => c.Gpr[5];
    static uint A2(Allegrex c) => c.Gpr[6];
    static uint A3(Allegrex c) => c.Gpr[7];
    static uint T0(Allegrex c) => c.Gpr[8];
    static uint T1(Allegrex c) => c.Gpr[9];
    static uint StackArg(Allegrex c, PspBus bus, int n) => bus.Read32(c.Gpr[29] + (uint)(16 + n * 4));
    static void Ret(Allegrex c, uint v) => c.Gpr[2] = v;
    static void Ret(Allegrex c, int v) => c.Gpr[2] = (uint)v;
    static void Ret64(Allegrex c, long v) { c.Gpr[2] = (uint)v; c.Gpr[3] = (uint)(v >> 32); }

    string ReadString(uint addr)
    {
        if (addr == 0) return "";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 256; i++)
        {
            byte b = _bus.Read8(addr + (uint)i);
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    void RegisterAll()
    {
        RegisterThreadMan();
        RegisterSysMem();
        RegisterDisplay();
        RegisterCtrl();
        RegisterAudio();
        RegisterIo();
        RegisterGe();
        RegisterUtils();
        RegisterLoadExec();
        RegisterModuleMgr();
        RegisterPower();
        RegisterRtc();
    }

    // ──────────────────── ThreadManForUser ────────────────────

    void RegisterThreadMan()
    {
        Reg(0x446D8DE6, "sceKernelCreateThread", c =>
        {
            string name = ReadString(A0(c));
            uint entry = A1(c);
            int priority = (int)A2(c);
            int stackSize = (int)A3(c);
            Ret(c, _kernel.CreateThread(name, entry, priority, stackSize));
        });

        Reg(0xF475845D, "sceKernelStartThread", c =>
        {
            Ret(c, _kernel.StartThread((int)A0(c), A1(c), A2(c)));
        });

        Reg(0xAA73C935, "sceKernelExitThread", c =>
        {
            Ret(c, _kernel.ExitThread((int)A0(c)));
        });

        Reg(0x809CE29B, "sceKernelExitDeleteThread", c =>
        {
            Ret(c, _kernel.ExitDeleteThread((int)A0(c)));
        });

        Reg(0xCEADEB47, "sceKernelDelayThread", c =>
        {
            _kernel.DelayThread(A0(c));
            Ret(c, 0);
        });

        Reg(0x68DA9E36, "sceKernelDelayThreadCB", c =>
        {
            _kernel.DelayThread(A0(c));
            Ret(c, 0);
        });

        Reg(0x9ACE131E, "sceKernelSleepThread", c =>
        {
            _kernel.SleepThread();
            Ret(c, 0);
        });

        Reg(0x82826F70, "sceKernelSleepThreadCB", c =>
        {
            // Sleep with callback check — for homebrew, just sleep
            _kernel.SleepThread();
            Ret(c, 0);
        });

        Reg(0xD59EAD2F, "sceKernelWakeupThread", c =>
        {
            _kernel.WakeupThread((int)A0(c));
            Ret(c, 0);
        });

        Reg(0x278C0106, "sceKernelWaitThreadEnd", c =>
        {
            Ret(c, _kernel.WaitThreadEnd((int)A0(c)));
        });

        Reg(0x293B45B8, "sceKernelGetThreadId", c =>
        {
            Ret(c, _kernel.GetThreadId());
        });

        Reg(0x94AA61EE, "sceKernelGetThreadCurrentPriority", c =>
        {
            Ret(c, _kernel.CurrentThread?.Priority ?? 32);
        });

        Reg(0x71BC9871, "sceKernelChangeThreadPriority", c =>
        {
            Ret(c, 0); // stub
        });

        Reg(0xEA748E31, "sceKernelChangeCurrentThreadAttr", c =>
        {
            Ret(c, 0);
        });

        // Callbacks
        Reg(0xE81CAF8F, "sceKernelCreateCallback", c =>
        {
            string name = ReadString(A0(c));
            uint funcAddr = A1(c);
            uint arg = A2(c);
            int cbId = _kernel.CreateCallback(name, funcAddr, arg);
            Log.Write(LogCat.Kernel, $"sceKernelCreateCallback '{name}' func={funcAddr:X8} → id={cbId}");
            Ret(c, cbId);
        });

        Reg(0xEDBA5844, "sceKernelDeleteCallback", c =>
        {
            Ret(c, 0);
        });

        Reg(0xC11BA8C4, "sceKernelNotifyCallback", c =>
        {
            Ret(c, 0);
        });

        Reg(0xBA4D533A, "sceKernelCancelCallback", c =>
        {
            Ret(c, 0);
        });

        Reg(0x349D6D6C, "sceKernelCheckCallback", c =>
        {
            Ret(c, 0); // no pending callbacks
        });

        // Semaphores
        Reg(0xD6DA4BA1, "sceKernelCreateSema", c =>
        {
            string name = ReadString(A0(c));
            int initCount = (int)A2(c);
            int maxCount = (int)A3(c);
            Ret(c, _kernel.CreateSema(name, initCount, maxCount));
        });

        Reg(0x3F53E640, "sceKernelSignalSema", c =>
        {
            Ret(c, _kernel.SignalSema((int)A0(c), (int)A1(c)));
        });

        Reg(0x4E3A1105, "sceKernelWaitSema", c =>
        {
            Ret(c, _kernel.WaitSema((int)A0(c), (int)A1(c)));
        });

        Reg(0x6D212BAC, "sceKernelWaitSemaCB", c =>
        {
            Ret(c, _kernel.WaitSema((int)A0(c), (int)A1(c)));
        });

        Reg(0x28B6489C, "sceKernelDeleteSema", c =>
        {
            Ret(c, _kernel.DeleteSema((int)A0(c)));
        });

        // Event Flags
        Reg(0x55C20A00, "sceKernelCreateEventFlag", c =>
        {
            string name = ReadString(A0(c));
            uint initPattern = A2(c);
            Ret(c, _kernel.CreateEventFlag(name, initPattern));
        });

        Reg(0x1FB15A32, "sceKernelSetEventFlag", c =>
        {
            Ret(c, _kernel.SetEventFlag((int)A0(c), A1(c)));
        });

        Reg(0x812346E4, "sceKernelClearEventFlag", c =>
        {
            Ret(c, _kernel.ClearEventFlag((int)A0(c), A1(c)));
        });

        Reg(0x402FCF22, "sceKernelWaitEventFlag", c =>
        {
            Ret(c, _kernel.WaitEventFlag((int)A0(c), A1(c), (int)A2(c), A3(c)));
        });

        Reg(0x328C546F, "sceKernelWaitEventFlagCB", c =>
        {
            Ret(c, _kernel.WaitEventFlag((int)A0(c), A1(c), (int)A2(c), A3(c)));
        });

        Reg(0xEF9E4C70, "sceKernelDeleteEventFlag", c =>
        {
            Ret(c, _kernel.DeleteEventFlag((int)A0(c)));
        });

        // Mutex
        Reg(0xB7D098C6, "sceKernelCreateMutex", c =>
        {
            string name = ReadString(A0(c));
            Ret(c, _kernel.CreateMutex(name, (int)A1(c), (int)A2(c)));
        });

        Reg(0xB011B11F, "sceKernelLockMutex", c =>
        {
            Ret(c, _kernel.LockMutex((int)A0(c), (int)A1(c)));
        });

        Reg(0x6B30100F, "sceKernelUnlockMutex", c =>
        {
            Ret(c, _kernel.UnlockMutex((int)A0(c), (int)A1(c)));
        });

        // Timing
        Reg(0x369ED59D, "sceKernelGetSystemTimeLow", c =>
        {
            Ret(c, (uint)_kernel.GetSystemTimeMicroseconds());
        });

        Reg(0x82BC5777, "sceKernelGetSystemTimeWide", c =>
        {
            Ret64(c, _kernel.GetSystemTimeMicroseconds());
        });

        Reg(0xDB738F35, "sceKernelGetSystemTime", c =>
        {
            uint ptr = A0(c);
            long t = _kernel.GetSystemTimeMicroseconds();
            if (ptr != 0) { _bus.Write32(ptr, (uint)t); _bus.Write32(ptr + 4, (uint)(t >> 32)); }
            Ret(c, 0);
        });

        Reg(0x110DEC9A, "sceKernelUSec2SysClock", c =>
        {
            uint usec = A0(c);
            uint outPtr = A1(c);
            if (outPtr != 0) { _bus.Write32(outPtr, usec); _bus.Write32(outPtr + 4, 0); }
            Ret(c, 0);
        });

        // DeleteThread
        Reg(0x9FA03CD3, "sceKernelDeleteThread", c =>
        {
            Ret(c, _kernel.DeleteThread((int)A0(c)));
        });

        // ReferThreadStatus
        Reg(0x17C1684E, "sceKernelReferThreadStatus", c =>
        {
            Ret(c, _kernel.ReferThreadStatus((int)A0(c), A1(c)));
        });

        // WaitThreadEndCB (alternate NID)
        Reg(0x278C0DF5, "sceKernelWaitThreadEndCB", c =>
        {
            Ret(c, _kernel.WaitThreadEnd((int)A0(c)));
        });

        // ── MsgPipe stubs (used by 19 demos) ──
        Reg(0x7C0DC2A0, "sceKernelCreateMsgPipe", c =>
        {
            string name = ReadString(A0(c));
            Log.Write(LogCat.Kernel, $"sceKernelCreateMsgPipe '{name}' (stub)");
            Ret(c, _kernel.AllocUidPublic());
        });
        Reg(0xF0B7DA1C, "sceKernelDeleteMsgPipe", c => Ret(c, 0));
        Reg(0x876DBFAD, "sceKernelSendMsgPipe", c => Ret(c, 0));
        Reg(0x884C9F90, "sceKernelTrySendMsgPipe", c => Ret(c, 0));
        Reg(0x74829B76, "sceKernelReceiveMsgPipe", c =>
        {
            uint sizePtr = A3(c);
            if (sizePtr != 0) _bus.Write32(sizePtr, 0);
            Ret(c, 0);
        });
        Reg(0xDF52098F, "sceKernelTryReceiveMsgPipe", c =>
        {
            uint sizePtr = A3(c);
            if (sizePtr != 0) _bus.Write32(sizePtr, 0);
            Ret(c, 0);
        });
        Reg(0x33BE4024, "sceKernelReferMsgPipeStatus", c => Ret(c, 0));

        // ── Mailbox stubs ──
        Reg(0x8125221D, "sceKernelCreateMbx", c =>
        {
            string name = ReadString(A0(c));
            Log.Write(LogCat.Kernel, $"sceKernelCreateMbx '{name}' (stub)");
            Ret(c, _kernel.AllocUidPublic());
        });
        Reg(0x86255ADA, "sceKernelDeleteMbx", c => Ret(c, 0));
        Reg(0xE9B3061E, "sceKernelSendMbx", c => Ret(c, 0));
        Reg(0x18260574, "sceKernelReceiveMbx", c => Ret(c, unchecked((uint)-1))); // SCE_KERNEL_ERROR_MBOX_NOMSG
        Reg(0x0D81716A, "sceKernelPollMbx", c => Ret(c, unchecked((uint)-1)));
        Reg(0x87D4DD36, "sceKernelCancelReceiveMbx", c => Ret(c, 0));
        Reg(0xA8E8C846, "sceKernelReferMbxStatus", c => Ret(c, 0));

        // ── Fixed Pool stubs ──
        Reg(0xC07BB470, "sceKernelCreateFpl", c =>
        {
            string name = ReadString(A0(c));
            Log.Write(LogCat.Kernel, $"sceKernelCreateFpl '{name}' (stub)");
            Ret(c, _kernel.AllocUidPublic());
        });
        Reg(0x623AE665, "sceKernelTryAllocateFpl", c =>
        {
            uint blockPtr = A2(c);
            uint size = A1(c);
            uint addr = _kernel.AllocUserMem(size, $"fpl_{A0(c)}");
            if (blockPtr != 0) _bus.Write32(blockPtr, addr);
            Ret(c, addr != 0 ? 0 : unchecked((uint)-1));
        });
    }

    // ──────────────────── SysMemUserForUser ────────────────────

    void RegisterSysMem()
    {
        Reg(0x237DBD4F, "sceKernelAllocPartitionMemory", c =>
        {
            int partition = (int)A0(c);
            string name = ReadString(A1(c));
            int type = (int)A2(c);
            uint size = A3(c);
            Ret(c, _kernel.AllocPartitionMemory(partition, name, type, size, 0));
        });

        Reg(0xB6D61D02, "sceKernelFreePartitionMemory", c =>
        {
            Ret(c, _kernel.FreePartitionMemory((int)A0(c)));
        });

        Reg(0x9D9A5BA1, "sceKernelGetBlockHeadAddr", c =>
        {
            Ret(c, _kernel.GetBlockHeadAddr((int)A0(c)));
        });

        Reg(0xA291F107, "sceKernelMaxFreeMemSize", c =>
        {
            Ret(c, _kernel.MaxFreeMemSize());
        });

        Reg(0xF919F628, "sceKernelTotalFreeMemSize", c =>
        {
            Ret(c, _kernel.TotalFreeMemSize());
        });

        Reg(0x3FC9AE6A, "sceKernelDevkitVersion", c =>
        {
            Ret(c, 0x06060010);
        });

        Reg(0x057E7380, "sceKernelQueryMemoryInfo", c =>
        {
            Ret(c, 0);
        });
    }

    // ──────────────────── sceDisplay ────────────────────

    void RegisterDisplay()
    {
        Reg(0x0E20F177, "sceDisplaySetMode", c =>
        {
            _display.SetMode((int)A0(c), (int)A1(c), (int)A2(c));
            Ret(c, 0);
        });

        Reg(0x289D82FE, "sceDisplaySetFrameBuf", c =>
        {
            _display.SetFrameBuf(A0(c), (int)A1(c), (int)A2(c), (int)A3(c));
            Ret(c, 0);
        });

        Reg(0x984C27E7, "sceDisplayWaitVblankStart", c =>
        {
            _display.WaitVblankStart();
            Ret(c, 0);
        });

        Reg(0x46F186C3, "sceDisplayWaitVblank", c =>
        {
            _display.WaitVblankStart();
            Ret(c, 0);
        });

        Reg(0x40AB34FC, "sceDisplayWaitVblankStartCB", c =>
        {
            _display.WaitVblankStart();
            Ret(c, 0);
        });

        Reg(0x8EB9EC49, "sceDisplayWaitVblankCB", c =>
        {
            _display.WaitVblankStart();
            Ret(c, 0);
        });

        Reg(0xEEDA2E54, "sceDisplayGetFrameBuf", c =>
        {
            uint topAddrPtr = A0(c);
            uint bufWidthPtr = A1(c);
            uint pixelFormatPtr = A2(c);
            if (topAddrPtr != 0) _bus.Write32(topAddrPtr, _display.FrameBufAddr);
            if (bufWidthPtr != 0) _bus.Write32(bufWidthPtr, (uint)_display.BufWidth);
            if (pixelFormatPtr != 0) _bus.Write32(pixelFormatPtr, (uint)_display.PixelFormat);
            Ret(c, 0);
        });

        Reg(0x9C6EAAD7, "sceDisplayGetVcount", c =>
        {
            Ret(c, _display.VCount);
        });

        Reg(0xDEA197D4, "sceDisplayGetMode", c =>
        {
            Ret(c, 0);
        });

        Reg(0xDBA6C4C4, "sceDisplayGetFramePerSec", c =>
        {
            c.Fpr[0] = 59.94f;
            Ret(c, 0);
        });

        // Alternate NID for sceDisplayWaitVblank (firmware variant)
        Reg(0x36CDFADE, "sceDisplayWaitVblank_alt", c =>
        {
            _display.WaitVblankStart();
            Ret(c, 0);
        });

        Reg(0x4D4E10EC, "sceDisplayIsVblank", c =>
        {
            Ret(c, _display.VCount >= 272 ? 1u : 0u);
        });
    }

    // ──────────────────── sceCtrl ────────────────────

    void RegisterCtrl()
    {
        Reg(0x6A2774F3, "sceCtrlSetSamplingCycle", c =>
        {
            _ctrl.SamplingCycle = (int)A0(c);
            Ret(c, 0);
        });

        Reg(0x1F4011E6, "sceCtrlSetSamplingMode", c =>
        {
            _ctrl.SamplingMode = (int)A0(c);
            Ret(c, 0);
        });

        Reg(0x1F803938, "sceCtrlReadBufferPositive", c =>
        {
            _ctrl.WriteToMemory(_bus, A0(c));
            Ret(c, 1);
        });

        Reg(0x3A622550, "sceCtrlPeekBufferPositive", c =>
        {
            _ctrl.WriteToMemory(_bus, A0(c));
            Ret(c, 1);
        });

        Reg(0x60B81F86, "sceCtrlReadBufferNegative", c =>
        {
            _ctrl.WriteToMemory(_bus, A0(c), true);
            Ret(c, 1);
        });

        Reg(0x0B588501, "sceCtrlReadLatch", c =>
        {
            uint latchPtr = A0(c);
            if (latchPtr != 0)
            {
                _bus.Write32(latchPtr, 0);      // uiMake
                _bus.Write32(latchPtr + 4, 0);  // uiBreak
                _bus.Write32(latchPtr + 8, _ctrl.Buttons);  // uiPress
                _bus.Write32(latchPtr + 12, ~_ctrl.Buttons); // uiRelease
            }
            Ret(c, 1);
        });
    }

    // ──────────────────── sceAudio ────────────────────

    void RegisterAudio()
    {
        Reg(0x5EC81C55, "sceAudioChReserve", c =>
        {
            Ret(c, _audio.ReserveChannel((int)A0(c), (int)A1(c), (int)A2(c)));
        });

        Reg(0x6FC46853, "sceAudioChRelease", c =>
        {
            Ret(c, _audio.ReleaseChannel((int)A0(c)));
        });

        Reg(0x136CAF51, "sceAudioOutputBlocking", c =>
        {
            Ret(c, _audio.OutputBlocking((int)A0(c), (int)A1(c), (int)A1(c), A2(c), _bus));
        });

        Reg(0x13F592BC, "sceAudioOutputPannedBlocking", c =>
        {
            Ret(c, _audio.OutputBlocking((int)A0(c), (int)A1(c), (int)A2(c), A3(c), _bus));
        });

        Reg(0x8C1009B2, "sceAudioOutput", c =>
        {
            Ret(c, _audio.OutputBlocking((int)A0(c), (int)A1(c), (int)A1(c), A2(c), _bus));
        });

        Reg(0xB7E1D8E7, "sceAudioGetChannelRestLen", c =>
        {
            Ret(c, _audio.GetChannelRestLen((int)A0(c)));
        });

        Reg(0x01562BA3, "sceAudioSRCChReserve", c =>
        {
            Ret(c, _audio.ReserveChannel(-1, (int)A0(c), (int)A1(c)));
        });

        Reg(0xE0727056, "sceAudioSRCOutputBlocking", c =>
        {
            Ret(c, _audio.OutputBlocking(0, (int)A0(c), (int)A0(c), A1(c), _bus));
        });
    }

    // ──────────────────── IoFileMgrForUser ────────────────────

    void RegisterIo()
    {
        Reg(0x109F50BC, "sceIoOpen", c =>
        {
            Ret(c, _io.Open(ReadString(A0(c)), (int)A1(c), (int)A2(c)));
        });

        Reg(0x810C4BC3, "sceIoClose", c =>
        {
            Ret(c, _io.Close((int)A0(c)));
        });

        Reg(0x6A638D83, "sceIoRead", c =>
        {
            Ret(c, _io.Read((int)A0(c), A1(c), A2(c)));
        });

        Reg(0x42EC03AC, "sceIoWrite", c =>
        {
            Ret(c, _io.Write((int)A0(c), A1(c), A2(c)));
        });

        Reg(0x27EB27B8, "sceIoLseek", c =>
        {
            long offset = (long)A1(c) | ((long)A2(c) << 32);
            Ret64(c, _io.Lseek((int)A0(c), offset, (int)A3(c)));
        });

        Reg(0x68963324, "sceIoLseek32", c =>
        {
            Ret(c, (int)_io.Lseek((int)A0(c), (int)A1(c), (int)A2(c)));
        });

        Reg(0x06A70004, "sceIoMkdir", c =>
        {
            Ret(c, _io.Mkdir(ReadString(A0(c))));
        });

        Reg(0x1117C65F, "sceIoRmdir", c =>
        {
            Ret(c, _io.Rmdir(ReadString(A0(c))));
        });

        Reg(0xB29DDF9C, "sceIoDopen", c =>
        {
            Ret(c, _io.Dopen(ReadString(A0(c))));
        });

        Reg(0xE3EB004C, "sceIoDread", c =>
        {
            Ret(c, _io.Dread((int)A0(c), A1(c)));
        });

        Reg(0xEB092469, "sceIoDclose", c =>
        {
            Ret(c, _io.Dclose((int)A0(c)));
        });

        Reg(0xACE946E8, "sceIoGetstat", c =>
        {
            Ret(c, _io.GetStat(ReadString(A0(c)), A1(c)));
        });

        Reg(0x55F4717D, "sceIoChdir", c =>
        {
            Ret(c, _io.Chdir(ReadString(A0(c))));
        });

        Reg(0x779103A0, "sceIoRename", c =>
        {
            Ret(c, _io.Rename(ReadString(A0(c)), ReadString(A1(c))));
        });

        Reg(0xF27A9C51, "sceIoRemove", c =>
        {
            Ret(c, _io.Remove(ReadString(A0(c))));
        });

        Reg(0x54F5FB11, "sceIoDevctl", c =>
        {
            string dev = ReadString(A0(c));
            uint cmd = A1(c);
            Log.Write(LogCat.IO, $"sceIoDevctl '{dev}' cmd=0x{cmd:X} (stub)");
            Ret(c, 0);
        });

        Reg(0x63632449, "sceIoIoctl", c =>
        {
            Log.Write(LogCat.IO, $"sceIoIoctl fd={A0(c)} cmd=0x{A1(c):X} (stub)");
            Ret(c, 0);
        });

        Reg(0xAB96437F, "sceIoSync", c =>
        {
            Ret(c, 0);
        });
    }

    // ──────────────────── sceGe_user ────────────────────

    void RegisterGe()
    {
        Reg(0xE47E40E4, "sceGeEdramGetAddr", c =>
        {
            Ret(c, 0x0400_0000);
        });

        Reg(0x1F6752AD, "sceGeEdramGetSize", c =>
        {
            Ret(c, PspBus.VramSize);
        });

        Reg(0xAB49E76A, "sceGeListEnQueue", c =>
        {
            Ret(c, _ge.EnqueueList(A0(c), A1(c)));
        });

        Reg(0x1C0D95A6, "sceGeListEnQueueHead", c =>
        {
            Ret(c, _ge.EnqueueList(A0(c), A1(c)));
        });

        Reg(0xE0D68148, "sceGeListUpdateStallAddr", c =>
        {
            _ge.UpdateStallAddr((int)A0(c), A1(c));
            Ret(c, 0);
        });

        Reg(0x03444EB4, "sceGeListSync", c =>
        {
            Ret(c, _ge.ListSync((int)A0(c), (int)A1(c)));
        });

        Reg(0xB287BD61, "sceGeDrawSync", c =>
        {
            Ret(c, _ge.DrawSync((int)A0(c)));
        });

        Reg(0x4C06E472, "sceGeContinue", c =>
        {
            _ge.Continue();
            Ret(c, 0);
        });

        Reg(0xA4FC06A4, "sceGeSetCallback", c =>
        {
            Ret(c, 0); // stub: return cb id 0
        });

        Reg(0x05DB22CE, "sceGeUnsetCallback", c =>
        {
            Ret(c, 0);
        });

        Reg(0xB77905EA, "sceGeEdramSetAddrTranslation", c =>
        {
            Ret(c, 0);
        });

        Reg(0xDC93CFEF, "sceGeGetCmd", c =>
        {
            Ret(c, _ge.GetCmd((int)A0(c)));
        });

        Reg(0x57C8945B, "sceGeGetMtx", c =>
        {
            Ret(c, 0); // stub
        });

        Reg(0x5FB86AB0, "sceGeListDeQueue", c =>
        {
            Ret(c, 0); // stub
        });
    }

    // ──────────────────── UtilsForUser ────────────────────

    void RegisterUtils()
    {
        Reg(0x79D1C3FA, "sceKernelDcacheWritebackAll", c => Ret(c, 0));
        Reg(0xB435DEC5, "sceKernelDcacheWritebackInvalidateAll", c => Ret(c, 0));
        Reg(0x920F104A, "sceKernelIcacheInvalidateAll", c => Ret(c, 0));
        Reg(0x3EE30821, "sceKernelDcacheWritebackRange", c => Ret(c, 0));
        Reg(0x34B9FA9E, "sceKernelDcacheWritebackInvalidateRange", c => Ret(c, 0));
        Reg(0xC2DF770E, "sceKernelIcacheInvalidateRange", c => Ret(c, 0));
        Reg(0xBFA98062, "sceKernelDcacheInvalidateRange", c => Ret(c, 0));

        Reg(0x27CC57F0, "sceKernelLibcTime", c =>
        {
            uint ptr = A0(c);
            uint time = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (ptr != 0) _bus.Write32(ptr, time);
            Ret(c, time);
        });

        Reg(0x91E4F6A7, "sceKernelLibcClock", c =>
        {
            Ret(c, (uint)(_kernel.GetSystemTimeMicroseconds() & 0xFFFFFFFF));
        });

        Reg(0x71EC4271, "sceKernelLibcGettimeofday", c =>
        {
            uint tvPtr = A0(c);
            if (tvPtr != 0)
            {
                long us = _kernel.GetSystemTimeMicroseconds();
                _bus.Write32(tvPtr, (uint)(us / 1000000));
                _bus.Write32(tvPtr + 4, (uint)(us % 1000000));
            }
            Ret(c, 0);
        });

        Reg(0x3FC9AE6A, "sceKernelDevkitVersion_dup", c =>
        {
            Ret(c, 0x06060010);
        });

        Reg(0x44A1C05B, "sceRtcGetCurrentTick", c =>
        {
            uint ptr = A0(c);
            long ticks = _kernel.GetSystemTimeMicroseconds();
            if (ptr != 0) { _bus.Write32(ptr, (uint)ticks); _bus.Write32(ptr + 4, (uint)(ticks >> 32)); }
            Ret(c, 0);
        });

        Reg(0xC41C2853, "sceRtcGetTickResolution", c =>
        {
            Ret(c, 1000000);
        });
    }

    // ──────────────────── LoadExecForUser ────────────────────

    void RegisterLoadExec()
    {
        Reg(0x05572A5F, "sceKernelExitGame", c =>
        {
            Log.Write(LogCat.Kernel, "sceKernelExitGame called");
            _kernel.ExitRequested = true;
            c.Halted = true;
        });

        Reg(0x2AC9954B, "sceKernelExitGameWithStatus", c =>
        {
            Log.Write(LogCat.Kernel, $"sceKernelExitGameWithStatus status={A0(c)}");
            _kernel.ExitRequested = true;
            c.Halted = true;
        });

        Reg(0x4AC57943, "sceKernelRegisterExitCallback", c =>
        {
            Log.Write(LogCat.Kernel, $"sceKernelRegisterExitCallback cbid={A0(c)}");
            Ret(c, 0);
        });

        Reg(0xBD2F1094, "sceKernelLoadExec", c =>
        {
            string file = ReadString(A0(c));
            Log.Write(LogCat.Kernel, $"sceKernelLoadExec '{file}' (stub - treating as exit)");
            _kernel.ExitRequested = true;
            c.Halted = true;
        });
    }

    // ──────────────────── ModuleMgrForUser ────────────────────

    void RegisterModuleMgr()
    {
        Reg(0x977DE386, "sceKernelLoadModule", c =>
        {
            string path = ReadString(A0(c));
            Log.Write(LogCat.Kernel, $"sceKernelLoadModule: {path} (stub)");
            Ret(c, _kernel.AllocUidPublic());
        });

        Reg(0x50F0C1EC, "sceKernelStartModule", c =>
        {
            Log.Write(LogCat.Kernel, $"sceKernelStartModule: uid={A0(c)} (stub)");
            Ret(c, 0);
        });

        Reg(0xD1FF982A, "sceKernelStopModule", c => Ret(c, 0));
        Reg(0x2E0911AA, "sceKernelUnloadModule", c => Ret(c, 0));

        Reg(0xD675EBB8, "sceKernelSelfStopUnloadModule", c =>
        {
            _kernel.ExitThread(0);
            Ret(c, 0);
        });

        Reg(0xC629AF26, "sceUtilityLoadModule", c =>
        {
            Log.Write(LogCat.Kernel, $"sceUtilityLoadModule module={A0(c)}");
            Ret(c, 0);
        });

        Reg(0xF7D8D092, "sceUtilityUnloadModule", c => Ret(c, 0));

        // StdioForUser stubs
        Reg(0x172D316E, "sceKernelStdin", c => Ret(c, 0));
        Reg(0xA6BAB2E9, "sceKernelStdout", c => Ret(c, 1));
        Reg(0xF78BA90A, "sceKernelStderr", c => Ret(c, 2));

        // Kprintf (debug print)
        Reg(0x84F370BC, "Kprintf", c =>
        {
            string fmt = ReadString(A0(c));
            Log.Write(LogCat.HLE, $"Kprintf: {fmt}");
            Ret(c, 0);
        });
    }

    // ──────────────────── scePower ────────────────────

    void RegisterPower()
    {
        Reg(0x04B7766E, "scePowerRegisterCallback", c => Ret(c, 0));
        Reg(0xEFD3C963, "scePowerTick", c => Ret(c, 0));
        Reg(0x87440F5E, "scePowerIsPowerOnline", c => Ret(c, 1));
        Reg(0x0AFD0D8B, "scePowerIsBatteryExist", c => Ret(c, 1));
        Reg(0x1E490401, "scePowerIsBatteryCharging", c => Ret(c, 0));
        Reg(0xD3075926, "scePowerIsLowBattery", c => Ret(c, 0));
        Reg(0x2085D15D, "scePowerGetBatteryLifePercent", c => Ret(c, 100));
        Reg(0x8EFB3FA2, "scePowerGetBatteryLifeTime", c => Ret(c, 300));
        Reg(0x28E12023, "scePowerGetBatteryTemp", c => Ret(c, 25));
        Reg(0x483CE86B, "scePowerGetBatteryVolt", c => Ret(c, 4200));
        Reg(0xD6D016EF, "scePowerLock", c => Ret(c, 0));
        Reg(0xCA3D34C1, "scePowerUnlock", c => Ret(c, 0));
        Reg(0x843FBF43, "scePowerSetCpuClockFrequency", c => Ret(c, 0));
        Reg(0xFEE03A2F, "scePowerGetCpuClockFrequency", c => Ret(c, 333));
        Reg(0x478FE6F5, "scePowerGetBusClockFrequency", c => Ret(c, 166));
        Reg(0xFDB5BFE9, "scePowerGetCpuClockFrequencyInt", c => Ret(c, 333));
        Reg(0x737486F2, "scePowerSetClockFrequency", c => Ret(c, 0));
    }

    // ──────────────────── sceRtc ────────────────────

    void RegisterRtc()
    {
        // Alternate NID for sceRtcGetCurrentTick (used by 15 demos)
        Reg(0x3F7AD767, "sceRtcGetCurrentTick_alt", c =>
        {
            uint ptr = A0(c);
            long ticks = _kernel.GetSystemTimeMicroseconds();
            if (ptr != 0) { _bus.Write32(ptr, (uint)ticks); _bus.Write32(ptr + 4, (uint)(ticks >> 32)); }
            Ret(c, 0);
        });

        Reg(0x05EF322C, "sceRtcGetDaysInMonth", c =>
        {
            int year = (int)A0(c);
            int month = (int)A1(c);
            int days = month switch
            {
                2 => (year % 4 == 0 && (year % 100 != 0 || year % 400 == 0)) ? 29 : 28,
                4 or 6 or 9 or 11 => 30,
                _ => 31
            };
            Ret(c, days);
        });

        Reg(0x57726BC1, "sceRtcGetDayOfWeek", c =>
        {
            int year = (int)A0(c);
            int month = (int)A1(c);
            int day = (int)A2(c);
            try { Ret(c, (int)new DateTime(year, month, day).DayOfWeek); }
            catch { Ret(c, 0); }
        });

        Reg(0x7ED29E40, "sceRtcSetTick", c => Ret(c, 0));

        Reg(0x6FF40ACC, "sceRtcGetTick", c =>
        {
            uint datePtr = A0(c);
            uint tickPtr = A1(c);
            if (tickPtr != 0)
            {
                long ticks = _kernel.GetSystemTimeMicroseconds();
                _bus.Write32(tickPtr, (uint)ticks);
                _bus.Write32(tickPtr + 4, (uint)(ticks >> 32));
            }
            Ret(c, 0);
        });
    }

    public IReadOnlyDictionary<uint, string> GetSyscallNames() => _syscallNames;
    public IReadOnlyDictionary<uint, uint> GetNidMap() => _nidToCode;
}

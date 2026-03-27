using System.Diagnostics;

namespace PspEmu;

/// <summary>
/// HLE PSP kernel: thread scheduling, memory management, synchronization primitives.
/// Syscalls from the CPU are dispatched here and resolved via NID to HLE functions.
/// </summary>
sealed class HleKernel
{
    // ── Thread management ──

    public sealed class PspThread
    {
        public int Uid;
        public string Name = "";
        public uint EntryPoint;
        public uint StackPointer;
        public uint StackBase;
        public int StackSize;
        public int Priority;
        public ThreadStatus Status;
        public uint[] Gpr = new uint[32];
        public uint Pc;
        public uint Hi, Lo;
        public float[] Fpr = new float[32];
        public float[] Vpr = new float[128];
        public uint ReturnValue;
        public long WakeupTime; // microseconds
        public int WaitSemaId;
        public int WaitEventFlagId;
        public uint WaitEventFlagPattern;
        public int WaitEventFlagMode;
        public int WaitThreadId;
    }

    public enum ThreadStatus { Running, Ready, Waiting, WaitingSema, WaitingEventFlag, WaitingDelay, WaitingThreadEnd, Dormant, Dead }

    public sealed class PspSemaphore
    {
        public int Uid;
        public string Name = "";
        public int Count;
        public int MaxCount;
    }

    public sealed class PspEventFlag
    {
        public int Uid;
        public string Name = "";
        public uint Pattern;
    }

    public sealed class PspMutex
    {
        public int Uid;
        public string Name = "";
        public int LockCount;
        public int OwnerThread;
    }

    public sealed class MemBlock
    {
        public int Uid;
        public string Name = "";
        public uint Address;
        public uint Size;
    }

    public sealed class PspCallback
    {
        public int Uid;
        public string Name = "";
        public uint FuncAddr;
        public uint Arg;
    }

    // ── State ──

    readonly List<PspThread> _threads = new();
    readonly List<PspSemaphore> _semaphores = new();
    readonly List<PspEventFlag> _eventFlags = new();
    readonly List<PspMutex> _mutexes = new();
    readonly List<MemBlock> _memBlocks = new();
    readonly List<PspCallback> _callbacks = new();

    int _nextUid = 1;
    PspThread? _currentThread;
    public PspThread? CurrentThread => _currentThread;

    readonly Allegrex _cpu;
    readonly PspBus _bus;
    HleModules _modules = null!;

    // Memory allocator state
    uint _userMemTop = 0x0880_0000 + 0x0020_0000; // start user alloc after 2MB for code
    const uint UserMemEnd = 0x0A00_0000; // end of user RAM (32MB)
    uint _stackTop = 0x09FF_F000; // stacks grow downward from near top

    // Timing
    readonly Stopwatch _timer = Stopwatch.StartNew();
    long _baseMicroseconds = 0;

    public long GetSystemTimeMicroseconds() => _baseMicroseconds + _timer.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;

    public bool ExitRequested { get; set; }

    public HleKernel(Allegrex cpu, PspBus bus)
    {
        _cpu = cpu;
        _bus = bus;
    }

    public void SetModules(HleModules modules) => _modules = modules;

    int AllocUid() => _nextUid++;
    public int AllocUidPublic() => _nextUid++;

    // ── Syscall dispatch ──

    readonly Dictionary<uint, int> _unhandledSyscallCounts = new();

    public void HandleSyscall(uint code)
    {
        if (_modules.TryDispatch(code, _cpu))
            return;

        _unhandledSyscallCounts.TryGetValue(code, out int count);
        _unhandledSyscallCounts[code] = count + 1;
        if (count < 3)
            Log.Warn(LogCat.Kernel, $"Unhandled syscall code={code:X5} at PC={_cpu.Pc - 8:X8}");
        else if (count == 3)
            Log.Warn(LogCat.Kernel, $"Unhandled syscall code={code:X5} (suppressing further)");

        _cpu.Gpr[2] = 0;
    }

    // ── Thread management ──

    public int CreateThread(string name, uint entry, int priority, int stackSize)
    {
        var thread = new PspThread
        {
            Uid = AllocUid(),
            Name = name,
            EntryPoint = entry,
            Priority = priority,
            StackSize = stackSize,
            Status = ThreadStatus.Dormant,
        };

        // Allocate stack
        _stackTop -= (uint)((stackSize + 0xFF) & ~0xFF);
        thread.StackBase = _stackTop;
        thread.StackPointer = _stackTop + (uint)stackSize - 16;

        _threads.Add(thread);
        Log.Write(LogCat.Kernel, $"CreateThread '{name}' uid={thread.Uid} entry={entry:X8} stack={thread.StackBase:X8}");
        return thread.Uid;
    }

    public int StartThread(int uid, uint argLen, uint argPtr)
    {
        var thread = _threads.Find(t => t.Uid == uid);
        if (thread == null) return -1;

        thread.Status = ThreadStatus.Ready;
        thread.Pc = thread.EntryPoint;
        Array.Clear(thread.Gpr);
        thread.Gpr[4] = argLen;   // a0 = argLen
        thread.Gpr[5] = argPtr;   // a1 = argPtr
        thread.Gpr[28] = thread.StackBase; // gp
        thread.Gpr[29] = thread.StackPointer; // sp
        thread.Gpr[31] = 0x0880_0000; // ra = a return address (will trigger exit)

        Log.Write(LogCat.Kernel, $"StartThread uid={uid} entry={thread.EntryPoint:X8}");

        // If this is the first/only thread, make it current
        if (_currentThread == null || _currentThread.Status != ThreadStatus.Running)
            SwitchToThread(thread);

        return 0;
    }

    public int ExitThread(int exitStatus)
    {
        if (_currentThread == null) return -1;
        _currentThread.Status = ThreadStatus.Dead;
        _currentThread.ReturnValue = (uint)exitStatus;
        Log.Write(LogCat.Kernel, $"ExitThread uid={_currentThread.Uid} status={exitStatus}");

        // Wake threads waiting for this one
        foreach (var t in _threads)
        {
            if (t.Status == ThreadStatus.WaitingThreadEnd && t.WaitThreadId == _currentThread.Uid)
            {
                t.Status = ThreadStatus.Ready;
                t.ReturnValue = (uint)exitStatus;
            }
        }

        ScheduleNext();
        return 0;
    }

    public int ExitDeleteThread(int exitStatus)
    {
        ExitThread(exitStatus);
        return 0;
    }

    public int DeleteThread(int uid)
    {
        var thread = _threads.Find(t => t.Uid == uid);
        if (thread == null) return -1;
        if (thread.Status == ThreadStatus.Running) return -1;
        _threads.Remove(thread);
        return 0;
    }

    public int ReferThreadStatus(int uid, uint infoPtr)
    {
        if (uid == 0) uid = _currentThread?.Uid ?? -1;
        var thread = _threads.Find(t => t.Uid == uid);
        if (thread == null) return -1;
        if (infoPtr != 0)
        {
            // SceKernelThreadInfo minimal: size(4), name(32), attr(4), status(4), entry(4), stack(4), stackSize(4), gpReg(4), initPri(4), curPri(4), waitType(4), waitId(4)
            uint size = _bus.Read32(infoPtr);
            if (size == 0) size = 104;
            // Write name at +4 (up to 32 bytes)
            for (int i = 0; i < 32 && i < thread.Name.Length; i++)
                _bus.Write8(infoPtr + 4 + (uint)i, (byte)thread.Name[i]);
            if (thread.Name.Length < 32)
                _bus.Write8(infoPtr + 4 + (uint)thread.Name.Length, 0);
            _bus.Write32(infoPtr + 0x24, (uint)thread.Status); // status
            _bus.Write32(infoPtr + 0x28, thread.EntryPoint);
            _bus.Write32(infoPtr + 0x2C, thread.StackBase);
            _bus.Write32(infoPtr + 0x30, (uint)thread.StackSize);
            _bus.Write32(infoPtr + 0x38, (uint)thread.Priority); // initPriority
            _bus.Write32(infoPtr + 0x3C, (uint)thread.Priority); // currentPriority
        }
        return 0;
    }

    public void DelayThread(uint microseconds)
    {
        if (_currentThread == null) return;
        _currentThread.Status = ThreadStatus.WaitingDelay;
        _currentThread.WakeupTime = GetSystemTimeMicroseconds() + microseconds;
        SaveThreadState(_currentThread);
        ScheduleNext();
        if (_cpu.Pc == 0 || _currentThread.Status != ThreadStatus.Running)
            _cpu.WaitingVblank = true;
    }

    public void SleepThread()
    {
        if (_currentThread == null) return;
        _currentThread.Status = ThreadStatus.Waiting;
        SaveThreadState(_currentThread);
        ScheduleNext();
        if (_currentThread.Status != ThreadStatus.Running)
            _cpu.WaitingVblank = true;
    }

    public void WakeupThread(int uid)
    {
        var thread = _threads.Find(t => t.Uid == uid);
        if (thread != null && thread.Status == ThreadStatus.Waiting)
            thread.Status = ThreadStatus.Ready;
    }

    public int WaitThreadEnd(int uid)
    {
        var target = _threads.Find(t => t.Uid == uid);
        if (target == null) return -1;
        if (target.Status == ThreadStatus.Dead)
            return (int)target.ReturnValue;

        if (_currentThread != null)
        {
            _currentThread.Status = ThreadStatus.WaitingThreadEnd;
            _currentThread.WaitThreadId = uid;
            SaveThreadState(_currentThread);
            ScheduleNext();
        }
        return 0;
    }

    public int GetThreadId() => _currentThread?.Uid ?? 0;

    // ── Semaphore ──

    public int CreateSema(string name, int initCount, int maxCount)
    {
        var sema = new PspSemaphore
        {
            Uid = AllocUid(),
            Name = name,
            Count = initCount,
            MaxCount = maxCount,
        };
        _semaphores.Add(sema);
        Log.Write(LogCat.Kernel, $"CreateSema '{name}' uid={sema.Uid} init={initCount} max={maxCount}");
        return sema.Uid;
    }

    public int SignalSema(int uid, int signal)
    {
        var sema = _semaphores.Find(s => s.Uid == uid);
        if (sema == null) return -1;
        sema.Count = Math.Min(sema.Count + signal, sema.MaxCount);

        // Wake waiting threads
        foreach (var t in _threads)
        {
            if (t.Status == ThreadStatus.WaitingSema && t.WaitSemaId == uid && sema.Count > 0)
            {
                sema.Count--;
                t.Status = ThreadStatus.Ready;
            }
        }
        return 0;
    }

    public int WaitSema(int uid, int count)
    {
        var sema = _semaphores.Find(s => s.Uid == uid);
        if (sema == null) return -1;
        if (sema.Count >= count)
        {
            sema.Count -= count;
            return 0;
        }

        if (_currentThread != null)
        {
            _currentThread.Status = ThreadStatus.WaitingSema;
            _currentThread.WaitSemaId = uid;
            SaveThreadState(_currentThread);
            ScheduleNext();
        }
        return 0;
    }

    public int DeleteSema(int uid)
    {
        _semaphores.RemoveAll(s => s.Uid == uid);
        return 0;
    }

    // ── Event Flag ──

    public int CreateEventFlag(string name, uint initPattern)
    {
        var ef = new PspEventFlag
        {
            Uid = AllocUid(),
            Name = name,
            Pattern = initPattern,
        };
        _eventFlags.Add(ef);
        return ef.Uid;
    }

    public int SetEventFlag(int uid, uint bits)
    {
        var ef = _eventFlags.Find(e => e.Uid == uid);
        if (ef == null) return -1;
        ef.Pattern |= bits;

        // Wake waiting threads
        foreach (var t in _threads)
        {
            if (t.Status == ThreadStatus.WaitingEventFlag && t.WaitEventFlagId == uid)
            {
                bool match = t.WaitEventFlagMode == 0
                    ? (ef.Pattern & t.WaitEventFlagPattern) != 0   // OR mode
                    : (ef.Pattern & t.WaitEventFlagPattern) == t.WaitEventFlagPattern; // AND mode
                if (match)
                {
                    t.Status = ThreadStatus.Ready;
                    if ((t.WaitEventFlagMode & 0x10) != 0)
                        ef.Pattern &= ~t.WaitEventFlagPattern;
                }
            }
        }
        return 0;
    }

    public int WaitEventFlag(int uid, uint pattern, int mode, uint outPatternPtr)
    {
        var ef = _eventFlags.Find(e => e.Uid == uid);
        if (ef == null) return -1;

        bool isAnd = (mode & 1) != 0;
        bool clear = (mode & 0x10) != 0;
        bool match = isAnd
            ? (ef.Pattern & pattern) == pattern
            : (ef.Pattern & pattern) != 0;

        if (match)
        {
            if (outPatternPtr != 0) _bus.Write32(outPatternPtr, ef.Pattern);
            if (clear) ef.Pattern &= ~pattern;
            return 0;
        }

        if (_currentThread != null)
        {
            _currentThread.Status = ThreadStatus.WaitingEventFlag;
            _currentThread.WaitEventFlagId = uid;
            _currentThread.WaitEventFlagPattern = pattern;
            _currentThread.WaitEventFlagMode = mode;
            SaveThreadState(_currentThread);
            ScheduleNext();
        }
        return 0;
    }

    public int ClearEventFlag(int uid, uint bits)
    {
        var ef = _eventFlags.Find(e => e.Uid == uid);
        if (ef == null) return -1;
        ef.Pattern &= bits;
        return 0;
    }

    public int DeleteEventFlag(int uid)
    {
        _eventFlags.RemoveAll(e => e.Uid == uid);
        return 0;
    }

    // ── Callback ──

    public int CreateCallback(string name, uint funcAddr, uint arg)
    {
        var cb = new PspCallback { Uid = AllocUid(), Name = name, FuncAddr = funcAddr, Arg = arg };
        _callbacks.Add(cb);
        return cb.Uid;
    }

    // ── Mutex ──

    public int CreateMutex(string name, int attr, int initCount)
    {
        var m = new PspMutex { Uid = AllocUid(), Name = name, LockCount = initCount, OwnerThread = -1 };
        _mutexes.Add(m);
        return m.Uid;
    }

    public int LockMutex(int uid, int count)
    {
        var m = _mutexes.Find(x => x.Uid == uid);
        if (m == null) return -1;
        m.LockCount += count;
        m.OwnerThread = _currentThread?.Uid ?? -1;
        return 0;
    }

    public int UnlockMutex(int uid, int count)
    {
        var m = _mutexes.Find(x => x.Uid == uid);
        if (m == null) return -1;
        m.LockCount = Math.Max(0, m.LockCount - count);
        return 0;
    }

    // ── Memory management ──

    public int AllocPartitionMemory(int partition, string name, int type, uint size, uint addr)
    {
        size = (size + 0xFF) & ~0xFFu; // align to 256 bytes

        uint allocAddr;
        if (type == 0 || type == 2) // Low or address-based
        {
            allocAddr = (addr != 0 && addr >= _userMemTop) ? addr : _userMemTop;
            _userMemTop = allocAddr + size;
        }
        else // High allocation
        {
            allocAddr = UserMemEnd - size;
        }

        var block = new MemBlock
        {
            Uid = AllocUid(),
            Name = name,
            Address = allocAddr,
            Size = size,
        };
        _memBlocks.Add(block);

        Log.Write(LogCat.Kernel, $"AllocMem '{name}' uid={block.Uid} addr={allocAddr:X8} size={size:X}");
        return block.Uid;
    }

    public int FreePartitionMemory(int uid)
    {
        _memBlocks.RemoveAll(b => b.Uid == uid);
        return 0;
    }

    public uint GetBlockHeadAddr(int uid)
    {
        var block = _memBlocks.Find(b => b.Uid == uid);
        return block?.Address ?? 0;
    }

    public uint AllocUserMem(uint size, string name)
    {
        size = (size + 0xFF) & ~0xFFu;
        if (_userMemTop + size > UserMemEnd) return 0;
        uint addr = _userMemTop;
        _userMemTop += size;
        return addr;
    }

    public uint MaxFreeMemSize()
    {
        return UserMemEnd - _userMemTop;
    }

    public uint TotalFreeMemSize()
    {
        return UserMemEnd - _userMemTop;
    }

    // ── Scheduling ──

    void SaveThreadState(PspThread thread)
    {
        Array.Copy(_cpu.Gpr, thread.Gpr, 32);
        Array.Copy(_cpu.Fpr, thread.Fpr, 32);
        Array.Copy(_cpu.Vpr, thread.Vpr, 128);
        thread.Pc = _cpu.Pc;
        thread.Hi = _cpu.Hi;
        thread.Lo = _cpu.Lo;
    }

    void RestoreThreadState(PspThread thread)
    {
        Array.Copy(thread.Gpr, _cpu.Gpr, 32);
        Array.Copy(thread.Fpr, _cpu.Fpr, 32);
        Array.Copy(thread.Vpr, _cpu.Vpr, 128);
        _cpu.Pc = thread.Pc;
        _cpu.Hi = thread.Hi;
        _cpu.Lo = thread.Lo;
    }

    void SwitchToThread(PspThread thread)
    {
        thread.Status = ThreadStatus.Running;
        _currentThread = thread;
        RestoreThreadState(thread);
    }

    void ScheduleNext()
    {
        // Wake delayed threads
        long now = GetSystemTimeMicroseconds();
        foreach (var t in _threads)
        {
            if (t.Status == ThreadStatus.WaitingDelay && now >= t.WakeupTime)
                t.Status = ThreadStatus.Ready;
        }

        // Find highest priority ready thread
        PspThread? best = null;
        foreach (var t in _threads)
        {
            if (t.Status == ThreadStatus.Ready)
            {
                if (best == null || t.Priority < best.Priority)
                    best = t;
            }
        }

        if (best != null)
        {
            SwitchToThread(best);
        }
        else
        {
            // No ready threads - check if any are delayed
            bool anyAlive = false;
            foreach (var t in _threads)
            {
                if (t.Status != ThreadStatus.Dead)
                { anyAlive = true; break; }
            }

            if (!anyAlive)
            {
                Log.Write(LogCat.Kernel, "All threads dead, halting CPU");
                _cpu.Halted = true;
            }
        }
    }

    /// <summary>Called each frame to update thread scheduling and timing.</summary>
    public void Update()
    {
        long now = GetSystemTimeMicroseconds();

        foreach (var t in _threads)
        {
            if (t.Status == ThreadStatus.WaitingDelay && now >= t.WakeupTime)
            {
                t.Status = ThreadStatus.Ready;
                if (_currentThread == null || _currentThread.Status != ThreadStatus.Running)
                    SwitchToThread(t);
            }
        }
    }

    /// <summary>Create the initial main thread for a loaded program.</summary>
    public void CreateMainThread(uint entry, uint gp, uint stackTop)
    {
        var thread = new PspThread
        {
            Uid = AllocUid(),
            Name = "main",
            EntryPoint = entry,
            Priority = 32,
            StackSize = 0x4000,
            Status = ThreadStatus.Running,
            StackBase = stackTop - 0x4000,
            StackPointer = stackTop - 16,
        };

        thread.Pc = entry;
        thread.Gpr[28] = gp;
        thread.Gpr[29] = thread.StackPointer;
        thread.Gpr[31] = 0; // ra = 0 (will trap on return)

        _threads.Add(thread);
        _currentThread = thread;

        // Load into CPU
        RestoreThreadState(thread);
        _cpu.Halted = false;

        Log.Write(LogCat.Kernel, $"Main thread created: entry={entry:X8} sp={thread.StackPointer:X8}");
    }

    public IReadOnlyList<PspThread> GetThreads() => _threads;
    public IReadOnlyList<PspSemaphore> GetSemaphores() => _semaphores;
    public IReadOnlyList<PspEventFlag> GetEventFlags() => _eventFlags;
    public IReadOnlyList<MemBlock> GetMemBlocks() => _memBlocks;
}

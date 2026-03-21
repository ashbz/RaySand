using System.Runtime.CompilerServices;

namespace PsxEmu;

/// <summary>
/// PSX CD-ROM controller. Handles register I/O at 0x1F801800–0x1F801803,
/// command execution, interrupt scheduling, and sector delivery.
///
/// KEY FIX: Each queued interrupt carries its own response data. When an
/// interrupt fires, the response FIFO is cleared and reloaded with that
/// interrupt's data. This prevents two-stage commands (Init, GetID, Pause, etc.)
/// from corrupting the response FIFO by pre-loading both responses.
/// </summary>
class PsxCdRom
{
    readonly Queue<byte> _paramBuf = new(16);
    readonly Queue<byte> _responseBuf = new(16);

    // Double-buffered sector delivery (matches ProjectPSX architecture):
    // _lastReadSector is filled by the Read tick.
    // _currentSector is filled only when the game writes "Want Data" (0x80).
    readonly byte[] _lastReadSector = new byte[DiscImage.BytesPerSector];
    readonly byte[] _currentSector = new byte[0x924];
    int _sectorReadPos;
    int _sectorDataLen;
    bool _sectorReady;
    bool _lastSectorReady;
    int _lastSectorDataLen;
    bool _hasReadSector; // true after any successful sector read (for GetLocL validity)

    byte _index;
    byte _ie;
    byte _if;
    byte _stat;
    bool _busy;

    int _seekLoc;
    int _readLoc;
    int _counter;

    bool _doubleSpeed;
    bool _sectorSizeRaw;
    bool _xaAdpcm;
    bool _xaFilter;
    byte _filterFile;
    byte _filterChannel;

    enum DriveMode { Idle, Seek, Read, TOC }
    DriveMode _mode = DriveMode.Idle;

    DiscImage? _disc;
    const int PSX_CLOCK = 33_868_800;

    public bool HasDisc => _disc != null;

    // ── Interrupt queue with per-interrupt response data ──────────────────────

    struct DelayedInterrupt
    {
        public int Delay;
        public byte Type;
        public byte[]? Response;
    }

    readonly List<DelayedInterrupt> _irqList = new();

    void QueueIrq(byte type, byte[]? response, int delay = 50_000)
    {
        _irqList.Add(new DelayedInterrupt { Delay = delay, Type = type, Response = response });
    }

    const byte INT1 = 1;
    const byte INT2 = 2;
    const byte INT3 = 3;
    const byte INT5 = 5;

    void DeliverIrq(DelayedInterrupt irq)
    {
        _if |= irq.Type;
        _busy = false;
        _responseBuf.Clear();
        if (irq.Response != null)
            foreach (byte b in irq.Response)
                _responseBuf.Enqueue(b);
        IrqDelivered++;
    }

    // ── Disc management ──────────────────────────────────────────────────────

    public void InsertDisc(DiscImage disc)
    {
        _disc = disc;
        _stat = 0x02;
        Log.Info("[CDROM] Disc inserted");
    }

    public void RemoveDisc()
    {
        _disc?.Dispose();
        _disc = null;
        _stat = 0x18;
        _mode = DriveMode.Idle;
        Log.Info("[CDROM] Disc removed");
    }

    /// <summary>
    /// Reset CD-ROM controller state. Preserves the disc reference.
    /// </summary>
    public void Reset()
    {
        _paramBuf.Clear();
        _responseBuf.Clear();
        _irqList.Clear();
        _sectorReadPos = 0;
        _sectorDataLen = 0;
        _sectorReady = false;
        _lastSectorReady = false;
        _lastSectorDataLen = 0;
        _hasReadSector = false;
        _index = 0;
        _ie = 0;
        _if = 0;
        _busy = false;
        _seekLoc = 0;
        _readLoc = 0;
        _counter = 0;
        _doubleSpeed = false;
        _sectorSizeRaw = false;
        _xaAdpcm = false;
        _xaFilter = false;
        _filterFile = 0;
        _filterChannel = 0;
        _mode = DriveMode.Idle;
        _stat = _disc != null ? (byte)0x02 : (byte)0x00;
        CmdCount = 0;
        IrqDelivered = 0;
        LastCmd = 0;
        ReadCount = 0;
        WriteCount = 0;
        WriteLog.Clear();
    }

    // ── Tick ──────────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Tick(int cycles)
    {
        _counter += cycles;

        if (_irqList.Count > 0)
        {
            var front = _irqList[0];
            front.Delay -= cycles;
            _irqList[0] = front;

            if (front.Delay <= 0 && _if == 0)
            {
                _irqList.RemoveAt(0);
                DeliverIrq(front);
            }
        }

        if ((_if & _ie) != 0)
            return true;

        switch (_mode)
        {
            case DriveMode.Idle:
                _counter = 0;
                return false;

            case DriveMode.Seek:
                if (_counter < PSX_CLOCK / 3 || _irqList.Count != 0)
                    return false;
                _counter = 0;
                _mode = DriveMode.Idle;

                bool seekFailed = _disc == null || _readLoc > _disc.TotalSectors + 150;
                if (seekFailed)
                {
                    _stat = 0x04;
                    QueueIrq(INT5, new[] { _stat });
                }
                else
                {
                    byte[] raw = _disc!.ReadSector(_readLoc);
                    Array.Copy(raw, _lastReadSector, DiscImage.BytesPerSector);
                    _hasReadSector = true;
                    _stat = 0x02;
                    QueueIrq(INT2, new[] { _stat });
                }
                break;

            case DriveMode.Read:
                int readSpeed = _doubleSpeed ? 150 : 75;
                if (_counter < PSX_CLOCK / readSpeed || _irqList.Count != 0)
                    return false;
                _counter = 0;

                if (_disc == null) return false;
                byte[] rawSector = _disc.ReadSector(_readLoc++);
                Array.Copy(rawSector, _lastReadSector, DiscImage.BytesPerSector);
                _hasReadSector = true;

                _lastSectorDataLen = _sectorSizeRaw ? 0x924 : 0x800;
                _lastSectorReady = true;

                _stat = 0x22;
                QueueIrq(INT1, new[] { _stat });
                break;

            case DriveMode.TOC:
                if (_counter < PSX_CLOCK / 75 || _irqList.Count != 0)
                    return false;
                _counter = 0;
                _mode = DriveMode.Idle;
                _stat = 0x02;
                QueueIrq(INT2, new[] { _stat });
                break;
        }

        return false;
    }

    // ── Register access ──────────────────────────────────────────────────────

    public int ReadCount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Read(uint addr)
    {
        ReadCount++;
        switch (addr)
        {
            case 0x1F80_1800: return StatusReg();
            case 0x1F80_1801:
                return _responseBuf.Count > 0 ? _responseBuf.Dequeue() : (byte)0xFF;
            case 0x1F80_1802:
                return ReadDataByte();
            case 0x1F80_1803:
                return (_index & 1) == 0
                    ? (byte)(0xE0 | _ie)
                    : (byte)(0xE0 | _if);
            default: return 0;
        }
    }

    public int WriteCount;
    public readonly List<string> WriteLog = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(uint addr, byte val)
    {
        WriteCount++;
        if (WriteLog.Count < 50)
            WriteLog.Add($"W 0x{addr:X8} val=0x{val:X2} idx={_index}");
        switch (addr)
        {
            case 0x1F80_1800:
                _index = (byte)(val & 3);
                break;

            case 0x1F80_1801:
                if (_index == 0)
                    ExecuteCommand(val);
                break;

            case 0x1F80_1802:
                if (_index == 0)
                    _paramBuf.Enqueue(val);
                else if (_index == 1)
                    _ie = (byte)(val & 0x1F);
                break;

            case 0x1F80_1803:
                if (_index == 0)
                {
                    if ((val & 0x80) != 0)
                    {
                        if (_sectorReady)
                            return;
                        if (_lastSectorReady)
                        {
                            if (_lastSectorDataLen == 0x800)
                                Array.Copy(_lastReadSector, 24, _currentSector, 0, 0x800);
                            else
                                Array.Copy(_lastReadSector, 12, _currentSector, 0, 0x924);
                            _sectorDataLen = _lastSectorDataLen;
                            _sectorReadPos = 0;
                            _sectorReady = true;
                        }
                    }
                    else
                    {
                        _sectorReady = false;
                        _sectorReadPos = 0;
                        _sectorDataLen = 0;
                    }
                }
                else if (_index == 1)
                {
                    _if &= (byte)~(val & 0x1F);
                    if (_irqList.Count > 0 && _irqList[0].Delay <= 0)
                    {
                        var next = _irqList[0];
                        _irqList.RemoveAt(0);
                        DeliverIrq(next);
                    }
                    if ((val & 0x40) == 0x40)
                        _paramBuf.Clear();
                }
                break;
        }
    }

    byte ReadDataByte()
    {
        if (!_sectorReady || _sectorReadPos >= _sectorDataLen)
            return 0;
        return _currentSector[_sectorReadPos++];
    }

    public ReadOnlySpan<byte> DmaRead(int wordCount)
    {
        int bytes = wordCount * 4;
        if (!_sectorReady || _sectorReadPos + bytes > _sectorDataLen)
        {
            _sectorReadPos = _sectorDataLen;
            return ReadOnlySpan<byte>.Empty;
        }
        var span = new ReadOnlySpan<byte>(_currentSector, _sectorReadPos, bytes);
        _sectorReadPos += bytes;
        return span;
    }

    byte StatusReg()
    {
        int s = 0;
        s |= _busy ? (1 << 7) : 0;
        s |= (_sectorReady && _sectorReadPos < _sectorDataLen) ? (1 << 6) : 0;
        s |= _responseBuf.Count > 0 ? (1 << 5) : 0;
        s |= _paramBuf.Count < 16 ? (1 << 4) : 0;
        s |= _paramBuf.Count == 0 ? (1 << 3) : 0;
        s |= _xaAdpcm ? (1 << 2) : 0;
        s |= _index;
        return (byte)s;
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public int CmdCount;
    public int IrqDelivered;
    public byte LastCmd;
    public byte LastIF => _if;
    public byte LastIE => _ie;

    void ExecuteCommand(byte cmd)
    {
        _irqList.Clear();
        _responseBuf.Clear();
        _busy = true;
        CmdCount++;
        LastCmd = cmd;

        switch (cmd)
        {
            case 0x01: CmdGetStat(); break;
            case 0x02: CmdSetLoc(); break;
            case 0x03: CmdPlay(); break;
            case 0x06: CmdReadN(); break;
            case 0x07: CmdMotorOn(); break;
            case 0x08: CmdStop(); break;
            case 0x09: CmdPause(); break;
            case 0x0A: CmdInit(); break;
            case 0x0B: CmdMute(); break;
            case 0x0C: CmdDemute(); break;
            case 0x0D: CmdSetFilter(); break;
            case 0x0E: CmdSetMode(); break;
            case 0x10: CmdGetLocL(); break;
            case 0x11: CmdGetLocP(); break;
            case 0x12: CmdSetSession(); break;
            case 0x13: CmdGetTN(); break;
            case 0x14: CmdGetTD(); break;
            case 0x15: CmdSeekL(); break;
            case 0x16: CmdSeekP(); break;
            case 0x19: CmdTest(); break;
            case 0x1A: CmdGetID(); break;
            case 0x1B: CmdReadS(); break;
            case 0x1E: CmdReadTOC(); break;
            default:
                Log.Info($"[CDROM] Unimplemented command 0x{cmd:X2}");
                QueueIrq(INT5, new byte[] { 0x11, 0x40 });
                break;
        }
    }

    // ── Individual commands ───────────────────────────────────────────────────

    void CmdGetStat()
    {
        if (_disc != null) { _stat = (byte)(_stat & ~0x18); _stat |= 0x02; }
        QueueIrq(INT3, new[] { _stat });
    }

    void CmdSetLoc()
    {
        byte mm = DeqParam(), ss = DeqParam(), ff = DeqParam();
        int minute = BcdToDec(mm), second = BcdToDec(ss), sector = BcdToDec(ff);
        _seekLoc = sector + second * 75 + minute * 60 * 75;
        if (_seekLoc < 0) _seekLoc = 0;
        QueueIrq(INT3, new[] { _stat });
    }

    void CmdPlay()
    {
        if (_paramBuf.Count > 0) _paramBuf.Dequeue();
        _readLoc = _seekLoc;
        _stat = 0x82;
        QueueIrq(INT3, new[] { _stat });
    }

    void CmdReadN()
    {
        _readLoc = _seekLoc;
        _stat = 0x22;
        QueueIrq(INT3, new[] { _stat });
        _mode = DriveMode.Read;
        _counter = 0;
    }

    void CmdMotorOn()
    {
        _stat = 0x02;
        QueueIrq(INT3, new[] { _stat });
        QueueIrq(INT2, new[] { _stat });
    }

    void CmdStop()
    {
        byte statNow = _stat;
        QueueIrq(INT3, new[] { statNow });
        _stat = 0x00;
        _mode = DriveMode.Idle;
        QueueIrq(INT2, new[] { _stat });
    }

    void CmdPause()
    {
        byte statNow = _stat;
        QueueIrq(INT3, new[] { statNow });
        _stat = 0x02;
        _mode = DriveMode.Idle;
        QueueIrq(INT2, new[] { _stat });
    }

    void CmdInit()
    {
        _stat = 0x02;
        _mode = DriveMode.Idle;
        _doubleSpeed = false;
        _sectorSizeRaw = false;
        _xaAdpcm = false;
        _xaFilter = false;
        QueueIrq(INT3, new[] { _stat });
        QueueIrq(INT2, new[] { _stat }, 100_000);
    }

    void CmdMute()
    {
        QueueIrq(INT3, new[] { _stat });
    }

    void CmdDemute()
    {
        QueueIrq(INT3, new[] { _stat });
    }

    void CmdSetFilter()
    {
        _filterFile = DeqParam();
        _filterChannel = DeqParam();
        QueueIrq(INT3, new[] { _stat });
    }

    void CmdSetMode()
    {
        byte mode = DeqParam();
        _doubleSpeed = (mode & 0x80) != 0;
        _xaAdpcm = (mode & 0x40) != 0;
        _sectorSizeRaw = (mode & 0x20) != 0;
        _xaFilter = (mode & 0x08) != 0;
        QueueIrq(INT3, new[] { _stat });
    }

    void CmdGetLocL()
    {
        if (!_hasReadSector || (_stat & 0x04) != 0)
        {
            QueueIrq(INT5, new byte[] { _stat, 0x80 });
            return;
        }
        var response = new byte[8];
        Array.Copy(_lastReadSector, 12, response, 0, 8);
        QueueIrq(INT3, response);
    }

    void CmdGetLocP()
    {
        if ((_stat & 0x04) != 0)
        {
            QueueIrq(INT5, new byte[] { _stat, 0x80 });
            return;
        }
        var (amm, ass, aff) = LbaToMsf(_readLoc);
        int relLba = Math.Max(0, _readLoc - 150);
        var (mm, ss, ff) = LbaToMsf(relLba);
        byte index = (byte)(_readLoc < 150 ? 0 : 1);

        QueueIrq(INT3, new byte[]
        {
            0x01, index,
            DecToBcd(mm), DecToBcd(ss), DecToBcd(ff),
            DecToBcd(amm), DecToBcd(ass), DecToBcd(aff),
        });
    }

    void CmdSetSession()
    {
        _paramBuf.Clear();
        _stat = 0x42;
        QueueIrq(INT3, new[] { _stat });
        QueueIrq(INT2, new[] { _stat });
    }

    void CmdGetTN()
    {
        byte trackCount = (byte)(_disc?.TrackCount ?? 0);
        QueueIrq(INT3, new byte[] { _stat, 0x01, DecToBcd(trackCount) });
    }

    void CmdGetTD()
    {
        byte trackBcd = DeqParam();
        int track = BcdToDec(trackBcd);

        if (track == 0 && _disc != null)
        {
            int lba = _disc.TotalSectors + 150;
            var (dm, ds, _) = LbaToMsf(lba);
            QueueIrq(INT3, new byte[] { _stat, DecToBcd(dm), DecToBcd(ds) });
        }
        else
        {
            var (dm, ds, _) = LbaToMsf(150);
            QueueIrq(INT3, new byte[] { _stat, DecToBcd(dm), DecToBcd(ds) });
        }
    }

    void CmdSeekL()
    {
        _readLoc = _seekLoc;
        _stat = 0x42;
        _mode = DriveMode.Seek;
        _counter = 0;
        QueueIrq(INT3, new[] { _stat });
    }

    void CmdSeekP()
    {
        _readLoc = _seekLoc;
        _stat = 0x42;
        _mode = DriveMode.Seek;
        _counter = 0;
        QueueIrq(INT3, new[] { _stat });
    }

    void CmdTest()
    {
        byte sub = DeqParam();
        switch (sub)
        {
            case 0x04:
                _stat = 0x02;
                QueueIrq(INT3, new[] { _stat });
                break;
            case 0x05:
                QueueIrq(INT3, new byte[] { 1, 0x45 });
                break;
            case 0x20:
                QueueIrq(INT3, new byte[] { 0x94, 0x09, 0x19, 0xC0 });
                break;
            case 0x22:
                QueueIrq(INT3, "for Europe"u8.ToArray());
                break;
            case 0x60:
                QueueIrq(INT3, new byte[] { 0 });
                break;
            default:
                Log.Info($"[CDROM] Unimplemented Test sub-command 0x{sub:X2}");
                break;
        }
    }

    void CmdGetID()
    {
        if (_disc == null)
        {
            _stat = 0x02;
            QueueIrq(INT3, new[] { _stat });
            QueueIrq(INT5, new byte[] { 0x08, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
            return;
        }

        _stat = 0x02;
        QueueIrq(INT3, new[] { _stat });
        QueueIrq(INT2, new byte[] { 0x02, 0x00, 0x20, 0x00, 0x53, 0x43, 0x45, 0x45 });
    }

    void CmdReadS()
    {
        _readLoc = _seekLoc;
        _stat = 0x22;
        QueueIrq(INT3, new[] { _stat });
        _mode = DriveMode.Read;
        _counter = 0;
    }

    void CmdReadTOC()
    {
        _mode = DriveMode.TOC;
        _counter = 0;
        QueueIrq(INT3, new[] { _stat });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    byte DeqParam() => _paramBuf.Count > 0 ? _paramBuf.Dequeue() : (byte)0;

    static byte DecToBcd(byte val) => (byte)(val + 6 * (val / 10));
    static int BcdToDec(byte val) => val - 6 * (val >> 4);

    static (byte mm, byte ss, byte ff) LbaToMsf(int lba)
    {
        int ff = lba % 75;
        lba /= 75;
        int ss = lba % 60;
        lba /= 60;
        return ((byte)lba, (byte)ss, (byte)ff);
    }
}

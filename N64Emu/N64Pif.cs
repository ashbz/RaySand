namespace N64Emu;

sealed class N64Pif
{
    public readonly byte[] BootRom = new byte[1984];
    public readonly byte[] Ram = new byte[64];
    public bool BootRomLoaded;

    public ushort Buttons;
    public sbyte StickX;
    public sbyte StickY;

    public byte[] Eeprom = new byte[2048];
    public int EepromSize = 512;

    public N64Bus? Bus;

    public void ResetRam()
    {
        Array.Clear(Ram);
    }

    public bool LoadBootRom(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            Array.Copy(data, BootRom, Math.Min(data.Length, BootRom.Length));
            BootRomLoaded = true;
            return true;
        }
        catch { return false; }
    }

    public void ProcessCommands()
    {
        byte control = Ram[63];

        if ((control & 1) != 0)
        {
            int i = 0;
            int channel = 0;
            while (i < 63 && channel < 6)
            {
                byte cmdLen = (byte)(Ram[i] & 0x3F);

                if (Ram[i] == 0xFE) break; // end of commands
                if (Ram[i] == 0x00) { i++; channel++; continue; } // skip channel
                if (Ram[i] == 0xFD) { i++; channel++; continue; } // channel reset
                if (Ram[i] == 0xFF) { i++; continue; } // padding

                if (i + 1 >= 63) break;

                int rxLenIdx = i + 1;
                byte rxByte = Ram[rxLenIdx];
                if (rxByte == 0xFE) break; // end of commands
                byte rxLen = (byte)(rxByte & 0x3F);

                int txOff = i + 2;
                int rxOff = txOff + cmdLen;
                int end = rxOff + rxLen;
                if (end > 63) break;

                ProcessChannel(channel, txOff, cmdLen, rxOff, rxLen, rxLenIdx);
                channel++;
                i = end;
            }
            Ram[63] &= unchecked((byte)~1); // clear bit 0
        }

        if ((control & 0x02) != 0)
        {
            // CIC challenge - stub: clear the flag
            Ram[63] &= unchecked((byte)~2);
        }

        if ((control & 0x08) != 0)
        {
            Ram[63] &= unchecked((byte)~8);
        }

        if ((control & 0x30) != 0)
        {
            Ram[63] = 0x80;
        }
    }

    void ProcessChannel(int channel, int txOff, int txLen, int rxOff, int rxLen, int rxLenIdx)
    {
        if (txLen == 0) return;
        byte cmd = Ram[txOff];

        if (channel <= 3)
        {
            switch (cmd)
            {
                case 0x00: // Info
                case 0xFF: // Reset
                    if (channel == 0)
                    {
                        if (rxLen >= 3)
                        {
                            Ram[rxOff + 0] = 0x05; // standard controller
                            Ram[rxOff + 1] = 0x00;
                            Ram[rxOff + 2] = 0x02; // no pak (0x01 = pak present)
                        }
                    }
                    else
                    {
                        Ram[rxLenIdx] |= 0x80; // not connected (error flag on rxLen byte)
                    }
                    break;

                case 0x01: // Read controller state
                    if (channel == 0 && rxLen >= 4)
                    {
                        Ram[rxOff + 0] = (byte)(Buttons >> 8);
                        Ram[rxOff + 1] = (byte)(Buttons & 0xFF);
                        Ram[rxOff + 2] = (byte)StickX;
                        Ram[rxOff + 3] = (byte)StickY;
                    }
                    else
                    {
                        Ram[rxLenIdx] |= 0x80;
                    }
                    break;

                case 0x02: // Read controller pak
                    for (int j = 0; j < rxLen; j++)
                        Ram[rxOff + j] = 0;
                    if (rxLen > 0) Ram[rxOff + rxLen - 1] = CalcDataCrc(Ram, rxOff, rxLen - 1);
                    break;

                case 0x03: // Write controller pak
                    break;
            }
        }
        else if (channel == 4) // EEPROM
        {
            switch (cmd)
            {
                case 0x00: // Info
                    if (rxLen >= 3)
                    {
                        Ram[rxOff + 0] = 0x00;
                        Ram[rxOff + 1] = 0x80;
                        Ram[rxOff + 2] = 0x00;
                    }
                    break;

                case 0x04: // Read EEPROM
                    if (txLen >= 2 && rxLen >= 8)
                    {
                        int block = Ram[txOff + 1];
                        int off = block * 8;
                        for (int j = 0; j < 8 && off + j < Eeprom.Length; j++)
                            Ram[rxOff + j] = Eeprom[off + j];
                    }
                    break;

                case 0x05: // Write EEPROM
                    if (txLen >= 10)
                    {
                        int block = Ram[txOff + 1];
                        int off = block * 8;
                        for (int j = 0; j < 8 && off + j < Eeprom.Length; j++)
                            Eeprom[off + j] = Ram[txOff + 2 + j];
                        if (rxLen >= 1) Ram[rxOff] = 0;
                    }
                    break;
            }
        }
        else
        {
            Ram[rxLenIdx] |= 0x80;
        }
    }

    static byte CalcDataCrc(byte[] data, int offset, int len)
    {
        byte crc = 0;
        for (int i = 0; i < len; i++)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                int xorBit = ((crc & 0x80) != 0) ? 1 : 0;
                crc <<= 1;
                if (((data[offset + i] >> bit) & 1) != 0) crc |= 1;
                if (xorBit != 0) crc ^= 0x85;
            }
        }
        for (int bit = 7; bit >= 0; bit--)
        {
            int xorBit = ((crc & 0x80) != 0) ? 1 : 0;
            crc <<= 1;
            if (xorBit != 0) crc ^= 0x85;
        }
        return crc;
    }

    public uint ReadRam32(uint offset)
    {
        offset &= 0x3C;
        return (uint)(Ram[offset] << 24 | Ram[offset + 1] << 16 |
                       Ram[offset + 2] << 8 | Ram[offset + 3]);
    }

    public void WriteRam32(uint offset, uint val)
    {
        offset &= 0x3C;
        Ram[offset + 0] = (byte)(val >> 24);
        Ram[offset + 1] = (byte)(val >> 16);
        Ram[offset + 2] = (byte)(val >> 8);
        Ram[offset + 3] = (byte)(val);
    }
}

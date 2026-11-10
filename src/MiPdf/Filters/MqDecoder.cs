namespace MiPdf.Filters;

/// <summary>
/// The MQ arithmetic decoder of JBIG2 (ITU-T T.88, Annex E) and JPEG 2000 (ISO/IEC 15444-1, Annex C): decodes
/// binary decisions with adaptive probability states; a context state is (index &lt;&lt; 1 | MPS). Data past the
/// end reads as 0xFF.
/// </summary>
internal sealed class MqDecoder
{
    // Qe, next index after MPS, next index after LPS, switch MPS
    private static readonly (int Qe, byte Nmps, byte Nlps, bool Switch)[] Table =
    {
        (0x5601, 1, 1, true), (0x3401, 2, 6, false), (0x1801, 3, 9, false), (0x0AC1, 4, 12, false),
        (0x0521, 5, 29, false), (0x0221, 38, 33, false), (0x5601, 7, 6, true), (0x5401, 8, 14, false),
        (0x4801, 9, 14, false), (0x3801, 10, 14, false), (0x3001, 11, 17, false), (0x2401, 12, 18, false),
        (0x1C01, 13, 20, false), (0x1601, 29, 21, false), (0x5601, 15, 14, true), (0x5401, 16, 14, false),
        (0x5101, 17, 15, false), (0x4801, 18, 16, false), (0x3801, 19, 17, false), (0x3401, 20, 18, false),
        (0x3001, 21, 19, false), (0x2801, 22, 19, false), (0x2401, 23, 20, false), (0x2201, 24, 21, false),
        (0x1C01, 25, 22, false), (0x1801, 26, 23, false), (0x1601, 27, 24, false), (0x1401, 28, 25, false),
        (0x1201, 29, 26, false), (0x1101, 30, 27, false), (0x0AC1, 31, 28, false), (0x09C1, 32, 29, false),
        (0x08A1, 33, 30, false), (0x0521, 34, 31, false), (0x0441, 35, 32, false), (0x02A1, 36, 33, false),
        (0x0221, 37, 34, false), (0x0141, 38, 35, false), (0x0111, 39, 36, false), (0x0085, 40, 37, false),
        (0x0049, 41, 38, false), (0x0025, 42, 39, false), (0x0015, 43, 40, false), (0x0009, 44, 41, false),
        (0x0005, 45, 42, false), (0x0001, 45, 43, false), (0x5601, 46, 46, false),
    };

    private readonly byte[] _data;
    private readonly int _end;
    private int _position;
    private int _chigh, _clow, _ct, _a;

    public MqDecoder(byte[] data, int start, int end)
    {
        _data = data;
        _end = Math.Min(end, data.Length);
        _position = start;
        _chigh = start < _end ? data[start] : 0xFF;
        _clow = 0;
        ByteIn();
        _chigh = ((_chigh << 7) & 0xFFFF) | ((_clow >> 9) & 0x7F);
        _clow = (_clow << 7) & 0xFFFF;
        _ct -= 7;
        _a = 0x8000;
    }

    private void ByteIn()
    {
        int current = _position < _end ? _data[_position] : 0xFF;
        if (current == 0xFF)
        {
            int next = _position + 1 < _end ? _data[_position + 1] : 0xFF;
            if (next > 0x8F)
            {
                _clow += 0xFF00;
                _ct = 8;
            }
            else
            {
                _position++;
                _clow += next << 9;
                _ct = 7;
            }
        }
        else
        {
            _position++;
            _clow += (_position < _end ? _data[_position] : 0xFF) << 8;
            _ct = 8;
        }
        if (_clow > 0xFFFF)
        {
            _chigh += _clow >> 16;
            _clow &= 0xFFFF;
        }
    }

    /// <summary>Decodes one bit with the context state <paramref name="contexts"/>[<paramref name="index"/>] (index &lt;&lt; 1 | mps).</summary>
    public int Decode(byte[] contexts, int index)
    {
        int state = contexts[index] >> 1, mps = contexts[index] & 1;
        var (qe, nmps, nlps, @switch) = Table[state];
        int d;
        _a -= qe;
        if (_chigh < qe)
        {
            // LPS exchange
            if (_a < qe)
            {
                _a = qe;
                d = mps;
                state = nmps;
            }
            else
            {
                _a = qe;
                d = 1 ^ mps;
                if (@switch) mps = d;
                state = nlps;
            }
        }
        else
        {
            _chigh -= qe;
            if ((_a & 0x8000) != 0) return mps;
            // MPS exchange
            if (_a < qe)
            {
                d = 1 ^ mps;
                if (@switch) mps = d;
                state = nlps;
            }
            else
            {
                d = mps;
                state = nmps;
            }
        }
        do
        {
            if (_ct == 0) ByteIn();
            _a <<= 1;
            _chigh = ((_chigh << 1) & 0xFFFF) | ((_clow >> 15) & 1);
            _clow = (_clow << 1) & 0xFFFF;
            _ct--;
        } while ((_a & 0x8000) == 0);
        contexts[index] = (byte)(state << 1 | mps);
        return d;
    }
}

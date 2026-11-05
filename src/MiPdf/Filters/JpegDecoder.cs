using MiPdf.Objects;

namespace MiPdf.Filters;

/// <summary>
/// DCTDecode (ISO 32000-2, 7.4.8; JPEG, ITU-T T.81): baseline, extended sequential and progressive Huffman-coded
/// images with 8-bit samples, one to four components, any subsampling and restart intervals. The result is the
/// samples interleaved per pixel, after the color transform (YCbCr to RGB, YCCK to CMYK) given by the Adobe marker
/// or /ColorTransform. The inverse DCT, upsampling and color conversion follow libjpeg ("islow", "fancy upsampling"),
/// so the pixels match other decoders. Arithmetic-coded, lossless and 12-bit JPEGs are not supported.
/// </summary>
internal static class JpegDecoder
{
    private const int MaxScans = 1000;

    private static readonly int[] ZigZag =
    {
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21,
        28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61,
        54, 47, 55, 62, 63,
    };

    private sealed class Component
    {
        public int Id, H, V, QuantTable;
        public int DcTable, AcTable, Predictor;
        public int Width, Height;               // samples of this component (ceil(image size * H / Hmax))
        public int BlocksPerLine, BlocksPerColumn; // blocks needed for the samples
        public int StoredBlocksPerLine;          // blocks per line including MCU padding
        public short[] Coefficients = Array.Empty<short>();
    }

    private sealed class HuffmanTable
    {
        public readonly int[] MaxCode = new int[18];
        public readonly int[] ValueOffset = new int[17];
        public byte[] Values = Array.Empty<byte>();

        public HuffmanTable(byte[] counts, byte[] values)
        {
            Values = values;
            int code = 0, index = 0;
            for (int length = 1; length <= 16; length++)
            {
                ValueOffset[length] = index - code;
                code += counts[length - 1];
                index += counts[length - 1];
                MaxCode[length] = counts[length - 1] > 0 ? code - 1 : -1;
                code <<= 1;
            }
            MaxCode[17] = int.MaxValue;
        }
    }

    public static byte[] Decode(byte[] data, PdfDictionary? parms, out int width, out int height, out int componentCount)
    {
        var decoder = new State(data);
        decoder.Parse();
        width = decoder.Width;
        height = decoder.Height;
        componentCount = decoder.Components.Count;
        // the Adobe marker overrides /ColorTransform (default: transform three components unless they are RGB)
        int transform = decoder.AdobeTransform ?? parms?.GetInt(PdfName.Get("ColorTransform"), -1) ?? -1;
        if (transform < 0) transform = componentCount == 3 && !decoder.IsRgb ? 1 : 0;
        return decoder.Output(convert: transform != 0);
    }

    private sealed class State(byte[] data)
    {
        private int _position;
        private readonly HuffmanTable?[] _dcTables = new HuffmanTable?[4];
        private readonly HuffmanTable?[] _acTables = new HuffmanTable?[4];
        private readonly int[][] _quantTables = new int[4][];
        private int _restartInterval;
        private bool _progressive;
        private int _maxH = 1, _maxV = 1, _mcusPerLine, _mcusPerColumn;

        public int Width, Height;
        public readonly List<Component> Components = new();
        public int? AdobeTransform;
        public bool IsRgb;

        // entropy decoder state
        private int _bitBuffer, _bitCount;
        private bool _markerHit;
        private int _eobRun;

        private int ReadByte() => _position < data.Length ? data[_position++] : throw new PdfException("Unexpected end of JPEG data.");

        private int ReadUInt16() => (ReadByte() << 8) | ReadByte();

        public void Parse()
        {
            if (ReadByte() != 0xFF || ReadByte() != 0xD8) throw new PdfException("Not a JPEG image (no SOI marker).");
            int scans = 0;
            bool frame = false;
            while (_position < data.Length)
            {
                int marker = ReadMarker();
                if (marker < 0 || marker == 0xD9) break; // EOI or end of data
                if (marker is >= 0xD0 and <= 0xD7 or 0x01) continue; // stray RST / TEM without length
                int length = ReadUInt16() - 2;
                if (length < 0 || _position + length > data.Length) throw new PdfException("Invalid JPEG segment length.");
                int end = _position + length;
                switch (marker)
                {
                    case 0xC0 or 0xC1 or 0xC2:
                        if (frame) throw new PdfException("JPEG with more than one frame.");
                        ReadFrame(marker == 0xC2);
                        frame = true;
                        break;
                    case 0xC3 or >= 0xC5 and <= 0xC7 or >= 0xC9 and <= 0xCB or >= 0xCD and <= 0xCF:
                        throw new PdfException("Unsupported JPEG process (lossless, hierarchical or arithmetic coding).");
                    case 0xC4:
                        ReadHuffmanTables(end);
                        break;
                    case 0xDB:
                        ReadQuantizationTables(end);
                        break;
                    case 0xDD:
                        _restartInterval = ReadUInt16();
                        break;
                    case 0xDA:
                        if (!frame) throw new PdfException("JPEG scan before the frame header.");
                        if (++scans > MaxScans) throw new PdfException("Too many JPEG scans.");
                        ReadScan(end);
                        continue; // the scan data follows; ReadScan positions after it
                    case 0xEE when length >= 12 && data[_position] == 'A' && data[_position + 1] == 'd' && data[_position + 2] == 'o'
                                   && data[_position + 3] == 'b' && data[_position + 4] == 'e':
                        AdobeTransform = data[_position + 11];
                        break;
                }
                _position = end;
            }
            if (!frame) throw new PdfException("JPEG without frame header.");
        }

        /// <summary>Next marker code (after 0xFF fill bytes); -1 at the end of the data.</summary>
        private int ReadMarker()
        {
            while (_position < data.Length && data[_position] != 0xFF) _position++; // skip garbage
            while (_position < data.Length && data[_position] == 0xFF) _position++;
            return _position < data.Length ? data[_position++] : -1;
        }

        private void ReadFrame(bool progressive)
        {
            _progressive = progressive;
            if (ReadByte() != 8) throw new PdfException("Only 8-bit JPEG samples are supported.");
            Height = ReadUInt16();
            Width = ReadUInt16();
            int count = ReadByte();
            if (Width <= 0 || Height <= 0) throw new PdfException("Invalid JPEG image size.");
            if (count is not (1 or 3 or 4)) throw new PdfException($"Unsupported number of JPEG components: {count}.");
            PdfLimits.Current.CheckImage(Width, Height);
            PdfLimits.Current.CheckDecodedSize((long)Width * Height * count);
            for (int i = 0; i < count; i++)
            {
                int id = ReadByte(), sampling = ReadByte(), q = ReadByte();
                int h = sampling >> 4, v = sampling & 15;
                if (h is < 1 or > 4 || v is < 1 or > 4 || q > 3) throw new PdfException("Invalid JPEG component.");
                Components.Add(new Component { Id = id, H = h, V = v, QuantTable = q });
            }
            IsRgb = count == 3 && Components[0].Id == 'R' && Components[1].Id == 'G' && Components[2].Id == 'B';
            _maxH = Components.Max(c => c.H);
            _maxV = Components.Max(c => c.V);
            _mcusPerLine = (Width + 8 * _maxH - 1) / (8 * _maxH);
            _mcusPerColumn = (Height + 8 * _maxV - 1) / (8 * _maxV);
            foreach (var c in Components)
            {
                c.Width = (Width * c.H + _maxH - 1) / _maxH;
                c.Height = (Height * c.V + _maxV - 1) / _maxV;
                c.BlocksPerLine = (c.Width + 7) / 8;
                c.BlocksPerColumn = (c.Height + 7) / 8;
                c.StoredBlocksPerLine = _mcusPerLine * c.H;
                long blocks = (long)c.StoredBlocksPerLine * _mcusPerColumn * c.V;
                PdfLimits.Current.CheckDecodedSize(blocks * 64 * 2);
                c.Coefficients = new short[blocks * 64];
            }
        }

        private void ReadHuffmanTables(int end)
        {
            while (_position < end)
            {
                int info = ReadByte();
                int tableClass = info >> 4, id = info & 15;
                if (tableClass > 1 || id > 3) throw new PdfException("Invalid JPEG Huffman table.");
                var counts = new byte[16];
                int total = 0;
                for (int i = 0; i < 16; i++) total += counts[i] = (byte)ReadByte();
                if (total > 256 || _position + total > end) throw new PdfException("Invalid JPEG Huffman table.");
                var values = data.AsSpan(_position, total).ToArray();
                _position += total;
                var table = new HuffmanTable(counts, values);
                if (tableClass == 0) _dcTables[id] = table;
                else _acTables[id] = table;
            }
        }

        private void ReadQuantizationTables(int end)
        {
            while (_position < end)
            {
                int info = ReadByte();
                int precision = info >> 4, id = info & 15;
                if (id > 3) throw new PdfException("Invalid JPEG quantization table.");
                var table = new int[64];
                for (int i = 0; i < 64; i++) table[ZigZag[i]] = precision == 0 ? ReadByte() : ReadUInt16();
                _quantTables[id] = table;
            }
        }

        private void ReadScan(int headerEnd)
        {
            int count = ReadByte();
            if (count is < 1 or > 4) throw new PdfException("Invalid JPEG scan.");
            var scan = new List<Component>();
            for (int i = 0; i < count; i++)
            {
                int id = ReadByte(), tables = ReadByte();
                var component = Components.FirstOrDefault(c => c.Id == id) ?? throw new PdfException("JPEG scan refers to an unknown component.");
                component.DcTable = tables >> 4;
                component.AcTable = tables & 15;
                if (component.DcTable > 3 || component.AcTable > 3) throw new PdfException("Invalid JPEG scan tables.");
                scan.Add(component);
            }
            int ss = ReadByte(), se = ReadByte(), approximation = ReadByte();
            int ah = approximation >> 4, al = approximation & 15;
            if (ss > 63 || se > 63 || ss > se && _progressive || al > 13) throw new PdfException("Invalid JPEG spectral selection.");
            if (!_progressive) (ss, se, ah, al) = (0, 63, 0, 0);
            _position = headerEnd;
            DecodeScan(scan, ss, se, ah, al);
        }

        // ------------------------------------------------------------------ entropy decoding

        private int ReadBit()
        {
            if (_bitCount == 0)
            {
                if (_markerHit || _position >= data.Length) return 0; // zero bits after a marker or at the end
                int b = data[_position];
                if (b == 0xFF)
                {
                    int next = _position + 1 < data.Length ? data[_position + 1] : 0xD9;
                    if (next != 0x00)
                    {
                        _markerHit = true; // a marker ends the entropy-coded data
                        return 0;
                    }
                    _position += 2;
                }
                else _position++;
                _bitBuffer = b;
                _bitCount = 8;
            }
            _bitCount--;
            return (_bitBuffer >> _bitCount) & 1;
        }

        private int ReadBits(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++) value = (value << 1) | ReadBit();
            return value;
        }

        /// <summary>Reads <paramref name="count"/> bits and extends them to a signed value (T.81, F.12).</summary>
        private int ReceiveExtend(int count)
        {
            if (count == 0) return 0;
            if (count > 16) throw new PdfException("Invalid JPEG coefficient.");
            int value = ReadBits(count);
            return value < 1 << (count - 1) ? value - (1 << count) + 1 : value;
        }

        private int DecodeHuffman(HuffmanTable? table)
        {
            if (table == null) throw new PdfException("JPEG scan uses an undefined Huffman table.");
            int code = 0;
            for (int length = 1; length <= 16; length++)
            {
                code = (code << 1) | ReadBit();
                if (code <= table.MaxCode[length])
                {
                    int index = table.ValueOffset[length] + code;
                    return (uint)index < (uint)table.Values.Length ? table.Values[index] : throw new PdfException("Invalid JPEG Huffman code.");
                }
            }
            throw new PdfException("Invalid JPEG Huffman code.");
        }

        private void DecodeScan(List<Component> scan, int ss, int se, int ah, int al)
        {
            _bitCount = 0;
            _markerHit = false;
            _eobRun = 0;
            foreach (var c in Components) c.Predictor = 0;
            bool single = scan.Count == 1;
            var component = scan[0];
            int mcus = single ? component.BlocksPerLine * component.BlocksPerColumn : _mcusPerLine * _mcusPerColumn;
            int sinceRestart = 0;
            for (int mcu = 0; mcu < mcus; mcu++)
            {
                if (_restartInterval > 0 && sinceRestart == _restartInterval)
                {
                    Restart();
                    sinceRestart = 0;
                }
                sinceRestart++;
                if (single)
                {
                    int row = mcu / component.BlocksPerLine, col = mcu % component.BlocksPerLine;
                    DecodeBlock(component, row * component.StoredBlocksPerLine + col, ss, se, ah, al);
                    continue;
                }
                int mcuRow = mcu / _mcusPerLine, mcuCol = mcu % _mcusPerLine;
                foreach (var c in scan)
                    for (int v = 0; v < c.V; v++)
                        for (int h = 0; h < c.H; h++)
                            DecodeBlock(c, (mcuRow * c.V + v) * c.StoredBlocksPerLine + mcuCol * c.H + h, ss, se, ah, al);
            }
            // skip to the next marker (the rest of the scan data, if any)
            if (!_markerHit)
                while (_position < data.Length && !(data[_position] == 0xFF && _position + 1 < data.Length && data[_position + 1] != 0x00))
                    _position++;
        }

        private void Restart()
        {
            _bitCount = 0;
            _markerHit = false;
            // the RSTn marker
            while (_position + 1 < data.Length && !(data[_position] == 0xFF && data[_position + 1] is >= 0xD0 and <= 0xD7)) _position++;
            if (_position + 1 < data.Length) _position += 2;
            foreach (var c in Components) c.Predictor = 0;
            _eobRun = 0;
        }

        private void DecodeBlock(Component c, int block, int ss, int se, int ah, int al)
        {
            var coefficients = c.Coefficients.AsSpan(block * 64, 64);
            if (!_progressive)
            {
                int t = DecodeHuffman(_dcTables[c.DcTable]);
                c.Predictor += ReceiveExtend(t);
                coefficients[0] = (short)c.Predictor;
                var ac = _acTables[c.AcTable];
                for (int k = 1; k < 64;)
                {
                    int rs = DecodeHuffman(ac), r = rs >> 4, s = rs & 15;
                    if (s == 0)
                    {
                        if (r != 15) break;
                        k += 16;
                        continue;
                    }
                    k += r;
                    if (k > 63) throw new PdfException("Invalid JPEG AC coefficients.");
                    coefficients[ZigZag[k]] = (short)ReceiveExtend(s);
                    k++;
                }
                return;
            }
            if (ss == 0)
            {
                // DC scans of progressive JPEGs
                if (ah == 0)
                {
                    int t = DecodeHuffman(_dcTables[c.DcTable]);
                    c.Predictor += ReceiveExtend(t);
                    coefficients[0] = (short)(c.Predictor << al);
                }
                else if (ReadBit() == 1) coefficients[0] |= (short)(1 << al);
                return;
            }
            if (ah == 0) DecodeAcFirst(coefficients, _acTables[c.AcTable], ss, se, al);
            else DecodeAcRefine(coefficients, _acTables[c.AcTable], ss, se, al);
        }

        private void DecodeAcFirst(Span<short> coefficients, HuffmanTable? table, int ss, int se, int al)
        {
            if (_eobRun > 0)
            {
                _eobRun--;
                return;
            }
            for (int k = ss; k <= se; k++)
            {
                int rs = DecodeHuffman(table), r = rs >> 4, s = rs & 15;
                if (s != 0)
                {
                    k += r;
                    if (k > 63) throw new PdfException("Invalid JPEG AC coefficients.");
                    coefficients[ZigZag[k]] = (short)(ReceiveExtend(s) * (1 << al));
                }
                else if (r == 15) k += 15;
                else
                {
                    _eobRun = (1 << r) - 1;
                    if (r > 0) _eobRun += ReadBits(r);
                    break;
                }
            }
        }

        /// <summary>Successive approximation of AC coefficients (T.81, G.1.2.3; as libjpeg's decode_mcu_AC_refine).</summary>
        private void DecodeAcRefine(Span<short> coefficients, HuffmanTable? table, int ss, int se, int al)
        {
            int p1 = 1 << al, m1 = -1 << al;
            int k = ss;
            if (_eobRun == 0)
            {
                for (; k <= se; k++)
                {
                    int rs = DecodeHuffman(table), r = rs >> 4, s = rs & 15;
                    if (s != 0)
                    {
                        if (s != 1) throw new PdfException("Invalid JPEG refinement data.");
                        s = ReadBit() == 1 ? p1 : m1;
                    }
                    else if (r != 15)
                    {
                        _eobRun = 1 << r;
                        if (r > 0) _eobRun += ReadBits(r);
                        break;
                    }
                    // skip r zero coefficients (refining the nonzero ones on the way), then place s
                    do
                    {
                        ref short coefficient = ref coefficients[ZigZag[k]];
                        if (coefficient != 0)
                            Refine(ref coefficient, p1, m1);
                        else if (--r < 0) break;
                        k++;
                    }
                    while (k <= se);
                    if (s != 0 && k <= se) coefficients[ZigZag[k]] = (short)s;
                }
            }
            if (_eobRun > 0)
            {
                // refine the remaining nonzero coefficients of this band
                for (; k <= se; k++)
                {
                    ref short coefficient = ref coefficients[ZigZag[k]];
                    if (coefficient != 0) Refine(ref coefficient, p1, m1);
                }
                _eobRun--;
            }
        }

        private void Refine(ref short coefficient, int p1, int m1)
        {
            if (ReadBit() == 1 && (coefficient & p1) == 0)
                coefficient = (short)(coefficient >= 0 ? coefficient + p1 : coefficient + m1);
        }

        // ------------------------------------------------------------------ output

        public byte[] Output(bool convert)
        {
            // component planes at their own resolution
            var planes = new byte[Components.Count][];
            for (int i = 0; i < Components.Count; i++) planes[i] = InverseDct(Components[i]);
            int count = Components.Count;
            var result = new byte[(long)Width * Height * count];
            var upsampled = new byte[count][];
            for (int i = 0; i < count; i++) upsampled[i] = Upsample(Components[i], planes[i]);
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int p = y * Width + x;
                    long o = (long)p * count;
                    if (count == 1)
                    {
                        result[o] = upsampled[0][p];
                        continue;
                    }
                    if (!convert)
                    {
                        for (int c = 0; c < count; c++) result[o + c] = upsampled[c][p];
                        continue;
                    }
                    var (r, g, b) = YccToRgb(upsampled[0][p], upsampled[1][p], upsampled[2][p]);
                    if (count == 3)
                    {
                        result[o] = r;
                        result[o + 1] = g;
                        result[o + 2] = b;
                    }
                    else
                    {
                        // YCCK to CMYK
                        result[o] = (byte)(255 - r);
                        result[o + 1] = (byte)(255 - g);
                        result[o + 2] = (byte)(255 - b);
                        result[o + 3] = upsampled[3][p];
                    }
                }
            }
            return result;
        }

        private byte[] InverseDct(Component c)
        {
            var quant = _quantTables[c.QuantTable] ?? throw new PdfException("JPEG component uses an undefined quantization table.");
            int stride = c.StoredBlocksPerLine * 8, rows = c.Coefficients.Length / 64 / c.StoredBlocksPerLine;
            var plane = new byte[(long)stride * rows * 8];
            Span<int> workspace = stackalloc int[64];
            for (int row = 0; row < rows; row++)
                for (int col = 0; col < c.StoredBlocksPerLine; col++)
                    Idct.IslowBlock(c.Coefficients.AsSpan((row * c.StoredBlocksPerLine + col) * 64, 64), quant, workspace,
                        plane.AsSpan(row * 8 * stride + col * 8), stride);
            return plane;
        }

        /// <summary>Scales a component plane to the image size (libjpeg's fancy upsampling for 2x1 and 2x2).</summary>
        private byte[] Upsample(Component c, byte[] plane)
        {
            int stride = c.StoredBlocksPerLine * 8;
            int factorH = _maxH / c.H, factorV = _maxV / c.V;
            var result = new byte[(long)Width * Height];
            bool exact = _maxH % c.H == 0 && _maxV % c.V == 0;
            if (exact && factorH == 1 && factorV == 1)
            {
                for (int y = 0; y < Height; y++) plane.AsSpan(y * stride, Width).CopyTo(result.AsSpan(y * Width));
                return result;
            }
            if (exact && factorH == 2 && factorV is 1 or 2)
            {
                int inWidth = c.Width, outWidth = inWidth * 2;
                var row = new byte[outWidth];
                for (int y = 0; y < Height; y++)
                {
                    if (factorV == 1) H2V1(plane.AsSpan(y * stride, inWidth), row);
                    else
                    {
                        int inRow = y / 2;
                        int neighbor = y % 2 == 0 ? Math.Max(inRow - 1, 0) : Math.Min(inRow + 1, c.Height - 1);
                        H2V2(plane.AsSpan(inRow * stride, inWidth), plane.AsSpan(neighbor * stride, inWidth), row);
                    }
                    row.AsSpan(0, Width).CopyTo(result.AsSpan(y * Width));
                }
                return result;
            }
            // other ratios: replicate samples
            for (int y = 0; y < Height; y++)
            {
                int sy = Math.Min(y * c.V / _maxV, c.Height - 1);
                for (int x = 0; x < Width; x++) result[y * Width + x] = plane[sy * stride + Math.Min(x * c.H / _maxH, c.Width - 1)];
            }
            return result;
        }

        private static void H2V1(ReadOnlySpan<byte> input, byte[] output)
        {
            int n = input.Length;
            if (n == 1)
            {
                output[0] = output[1] = input[0];
                return;
            }
            output[0] = input[0];
            output[1] = (byte)((input[0] * 3 + input[1] + 2) >> 2);
            for (int i = 1; i < n - 1; i++)
            {
                int value = input[i] * 3;
                output[2 * i] = (byte)((value + input[i - 1] + 1) >> 2);
                output[2 * i + 1] = (byte)((value + input[i + 1] + 2) >> 2);
            }
            output[2 * n - 2] = (byte)((input[n - 1] * 3 + input[n - 2] + 1) >> 2);
            output[2 * n - 1] = input[n - 1];
        }

        private static void H2V2(ReadOnlySpan<byte> input, ReadOnlySpan<byte> neighbor, byte[] output)
        {
            int n = input.Length;
            int thisSum = input[0] * 3 + neighbor[0];
            if (n == 1)
            {
                output[0] = output[1] = (byte)((thisSum * 4 + 8) >> 4);
                return;
            }
            int nextSum = input[1] * 3 + neighbor[1];
            output[0] = (byte)((thisSum * 4 + 8) >> 4);
            output[1] = (byte)((thisSum * 3 + nextSum + 7) >> 4);
            int lastSum = thisSum;
            thisSum = nextSum;
            for (int i = 2; i < n; i++)
            {
                nextSum = input[i] * 3 + neighbor[i];
                output[2 * i - 2] = (byte)((thisSum * 3 + lastSum + 8) >> 4);
                output[2 * i - 1] = (byte)((thisSum * 3 + nextSum + 7) >> 4);
                lastSum = thisSum;
                thisSum = nextSum;
            }
            output[2 * n - 2] = (byte)((thisSum * 3 + lastSum + 8) >> 4);
            output[2 * n - 1] = (byte)((thisSum * 4 + 7) >> 4);
        }

        // libjpeg's YCbCr to RGB conversion (jdcolor.c), 16-bit fixed point
        private static (byte R, byte G, byte B) YccToRgb(int y, int cb, int cr)
        {
            const int half = 1 << 15;
            cb -= 128;
            cr -= 128;
            int r = y + ((91881 * cr + half) >> 16);
            int g = y + ((-22554 * cb - 46802 * cr + half) >> 16);
            int b = y + ((116130 * cb + half) >> 16);
            return (Clamp(r), Clamp(g), Clamp(b));
        }

        private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
    }

    /// <summary>libjpeg's accurate integer inverse DCT (jidctint.c, "islow").</summary>
    private static class Idct
    {
        private const int ConstBits = 13, Pass1Bits = 2;
        private const int Fix0298631336 = 2446, Fix0390180644 = 3196, Fix0541196100 = 4433, Fix0765366865 = 6270;
        private const int Fix0899976223 = 7373, Fix1175875602 = 9633, Fix1501321110 = 12299, Fix1847759065 = 15137;
        private const int Fix1961570560 = 16069, Fix2053119869 = 16819, Fix2562915447 = 20995, Fix3072711026 = 25172;

        private static int Descale(long x, int n) => (int)((x + (1L << (n - 1))) >> n);

        public static void IslowBlock(ReadOnlySpan<short> input, int[] quant, Span<int> ws, Span<byte> output, int stride)
        {
            // pass 1: columns
            for (int c = 0; c < 8; c++)
            {
                if (input[8 + c] == 0 && input[16 + c] == 0 && input[24 + c] == 0 && input[32 + c] == 0 &&
                    input[40 + c] == 0 && input[48 + c] == 0 && input[56 + c] == 0)
                {
                    int dc = input[c] * quant[c] << Pass1Bits;
                    for (int r = 0; r < 8; r++) ws[r * 8 + c] = dc;
                    continue;
                }
                long z2 = input[16 + c] * quant[16 + c], z3 = input[48 + c] * quant[48 + c];
                long z1 = (z2 + z3) * Fix0541196100;
                long tmp2 = z1 + z3 * -Fix1847759065;
                long tmp3 = z1 + z2 * Fix0765366865;
                z2 = input[c] * quant[c];
                z3 = input[32 + c] * quant[32 + c];
                long tmp0 = (z2 + z3) << ConstBits, tmp1 = (z2 - z3) << ConstBits;
                long tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;

                tmp0 = input[56 + c] * quant[56 + c];
                tmp1 = input[40 + c] * quant[40 + c];
                tmp2 = input[24 + c] * quant[24 + c];
                tmp3 = input[8 + c] * quant[8 + c];
                Odd(ref tmp0, ref tmp1, ref tmp2, ref tmp3);

                const int shift = ConstBits - Pass1Bits;
                ws[c] = Descale(tmp10 + tmp3, shift);
                ws[56 + c] = Descale(tmp10 - tmp3, shift);
                ws[8 + c] = Descale(tmp11 + tmp2, shift);
                ws[48 + c] = Descale(tmp11 - tmp2, shift);
                ws[16 + c] = Descale(tmp12 + tmp1, shift);
                ws[40 + c] = Descale(tmp12 - tmp1, shift);
                ws[24 + c] = Descale(tmp13 + tmp0, shift);
                ws[32 + c] = Descale(tmp13 - tmp0, shift);
            }
            // pass 2: rows
            for (int r = 0; r < 8; r++)
            {
                var row = ws.Slice(r * 8, 8);
                var outRow = output.Slice(r * stride, 8);
                if (row[1] == 0 && row[2] == 0 && row[3] == 0 && row[4] == 0 && row[5] == 0 && row[6] == 0 && row[7] == 0)
                {
                    byte dc = Limit(Descale(row[0], Pass1Bits + 3) + 128);
                    outRow.Fill(dc);
                    continue;
                }
                long z2 = row[2], z3 = row[6];
                long z1 = (z2 + z3) * Fix0541196100;
                long tmp2 = z1 + z3 * -Fix1847759065;
                long tmp3 = z1 + z2 * Fix0765366865;
                long tmp0 = ((long)row[0] + row[4]) << ConstBits, tmp1 = ((long)row[0] - row[4]) << ConstBits;
                long tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;

                tmp0 = row[7];
                tmp1 = row[5];
                tmp2 = row[3];
                tmp3 = row[1];
                Odd(ref tmp0, ref tmp1, ref tmp2, ref tmp3);

                const int shift = ConstBits + Pass1Bits + 3;
                outRow[0] = Limit(Descale(tmp10 + tmp3, shift) + 128);
                outRow[7] = Limit(Descale(tmp10 - tmp3, shift) + 128);
                outRow[1] = Limit(Descale(tmp11 + tmp2, shift) + 128);
                outRow[6] = Limit(Descale(tmp11 - tmp2, shift) + 128);
                outRow[2] = Limit(Descale(tmp12 + tmp1, shift) + 128);
                outRow[5] = Limit(Descale(tmp12 - tmp1, shift) + 128);
                outRow[3] = Limit(Descale(tmp13 + tmp0, shift) + 128);
                outRow[4] = Limit(Descale(tmp13 - tmp0, shift) + 128);
            }
        }

        /// <summary>The odd part of the 8-point IDCT: inputs 7, 5, 3, 1 in, the four odd terms out.</summary>
        private static void Odd(ref long tmp0, ref long tmp1, ref long tmp2, ref long tmp3)
        {
            long z1 = tmp0 + tmp3, z2 = tmp1 + tmp2, z3 = tmp0 + tmp2, z4 = tmp1 + tmp3;
            long z5 = (z3 + z4) * Fix1175875602;
            tmp0 *= Fix0298631336;
            tmp1 *= Fix2053119869;
            tmp2 *= Fix3072711026;
            tmp3 *= Fix1501321110;
            z1 *= -Fix0899976223;
            z2 *= -Fix2562915447;
            z3 *= -Fix1961570560;
            z4 *= -Fix0390180644;
            z3 += z5;
            z4 += z5;
            tmp0 += z1 + z3;
            tmp1 += z2 + z4;
            tmp2 += z2 + z3;
            tmp3 += z1 + z4;
        }

        private static byte Limit(int value) => (byte)Math.Clamp(value, 0, 255);
    }
}

using System.Buffers.Binary;

namespace MiPdf.Filters;

/// <summary>A decoded JPEG 2000 image: one sample array per channel at full image size.</summary>
internal sealed class JpxImage
{
    public int Width, Height;

    /// <summary>The channels (after palette expansion and color transform), samples 0 to 2^precision - 1.</summary>
    public required int[][] Channels;

    public required int[] Precision;

    /// <summary>The enumerated color space of the JP2 header (16 sRGB, 17 gray, 18 sYCC, 12 CMYK, ...), 0 if none.</summary>
    public int ColorSpace;

    /// <summary>True if the JP2 header specifies an ICC profile.</summary>
    public bool HasIcc;

    /// <summary>The channel with opacity (JP2 channel definition), -1 if none.</summary>
    public int AlphaChannel = -1;

    /// <summary>The number of color channels (without the alpha channel).</summary>
    public int ColorChannels => AlphaChannel >= 0 ? Channels.Length - 1 : Channels.Length;

    /// <summary>A channel scaled to 8 bits.</summary>
    public byte[] Channel8(int index)
    {
        var source = Channels[index];
        int precision = Precision[index];
        var result = new byte[source.Length];
        if (precision == 8)
        {
            for (int i = 0; i < source.Length; i++) result[i] = (byte)source[i];
        }
        else
        {
            int max = (1 << precision) - 1;
            for (int i = 0; i < source.Length; i++) result[i] = (byte)((source[i] * 255 + max / 2) / max);
        }
        return result;
    }
}

/// <summary>
/// JPXDecode (ISO 32000-2, 7.4.9; ISO/IEC 15444-1): JPEG 2000 codestreams and JP2 files. Supports all progression
/// orders and progression order changes, tiles and tile-parts, precincts, packed packet headers (PPM, PPT), SOP/EPH
/// markers, all code-block coding modes (bypass, reset, termination, vertical causal, segmentation symbols),
/// region of interest (max-shift), scalar quantization, the 5/3 and 9/7 wavelets, the reversible and irreversible
/// component transforms, component subsampling, palettes and channel definitions.
/// </summary>
internal static class JpxDecoder
{
    public static JpxImage Decode(byte[] data)
    {
        try
        {
            if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0x4F) return new Codestream(data, 0, data.Length).Decode();
            return DecodeJp2(data);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException
                                       or InvalidOperationException or OverflowException or DivideByZeroException)
        {
            throw new PdfException("Damaged JPEG 2000 data.", ex);
        }
    }

    // ------------------------------------------------------------------ JP2 boxes

    private static JpxImage DecodeJp2(byte[] data)
    {
        int colorSpace = 0;
        bool icc = false;
        int[][]? palette = null;
        int[]? paletteDepth = null;
        List<(int Component, int Type, int Column)>? mapping = null;
        List<(int Channel, int Type, int Association)>? definitions = null;
        JpxImage? image = null;

        void Boxes(int start, int end)
        {
            int p = start;
            while (p + 8 <= end)
            {
                long length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p));
                string type = System.Text.Encoding.ASCII.GetString(data, p + 4, 4);
                int header = 8;
                if (length == 1)
                {
                    length = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(p + 8));
                    header = 16;
                }
                else if (length == 0) length = end - p;
                int contentEnd = (int)Math.Min(end, p + length);
                int c = p + header;
                switch (type)
                {
                    case "jp2h":
                        Boxes(c, contentEnd);
                        break;
                    case "colr":
                        if (colorSpace == 0 && !icc)
                        {
                            if (data[c] == 1) colorSpace = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(c + 3));
                            else if (data[c] is 2 or 3) icc = true;
                        }
                        break;
                    case "pclr":
                    {
                        int entries = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(c));
                        int columns = data[c + 2];
                        paletteDepth = new int[columns];
                        for (int i = 0; i < columns; i++) paletteDepth[i] = (data[c + 3 + i] & 0x7F) + 1;
                        palette = new int[columns][];
                        for (int i = 0; i < columns; i++) palette[i] = new int[entries];
                        int q = c + 3 + columns;
                        for (int e = 0; e < entries; e++)
                            for (int i = 0; i < columns; i++)
                            {
                                int bytes = (paletteDepth[i] + 7) / 8, v = 0;
                                for (int b = 0; b < bytes; b++) v = (v << 8) | data[q++];
                                palette[i][e] = v;
                            }
                        break;
                    }
                    case "cmap":
                        mapping = new();
                        for (int q = c; q + 4 <= contentEnd; q += 4)
                            mapping.Add((BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(q)), data[q + 2], data[q + 3]));
                        break;
                    case "cdef":
                    {
                        definitions = new();
                        int n = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(c));
                        for (int i = 0; i < n; i++)
                        {
                            int q = c + 2 + 6 * i;
                            definitions.Add((BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(q)), BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(q + 2)),
                                BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(q + 4))));
                        }
                        break;
                    }
                    case "jp2c":
                        image ??= new Codestream(data, c, contentEnd).Decode();
                        break;
                }
                p = contentEnd;
                if (length <= 0) break;
            }
        }

        Boxes(0, data.Length);
        image ??= TryRawCodestream(data) ?? throw new PdfException("JPEG 2000 data without codestream.");
        image.ColorSpace = colorSpace;
        image.HasIcc = icc;

        // palette: channels from palette columns
        if (palette != null && mapping != null)
        {
            var channels = new List<int[]>();
            var precision = new List<int>();
            foreach (var (component, type, column) in mapping)
            {
                if (type == 1 && column < palette.Length)
                {
                    var source = image.Channels[component];
                    var target = new int[source.Length];
                    var p = palette[column];
                    for (int i = 0; i < source.Length; i++) target[i] = p[Math.Clamp(source[i], 0, p.Length - 1)];
                    channels.Add(target);
                    precision.Add(paletteDepth![column]);
                }
                else
                {
                    channels.Add(image.Channels[component]);
                    precision.Add(image.Precision[component]);
                }
            }
            image = new JpxImage
            {
                Width = image.Width, Height = image.Height, Channels = channels.ToArray(), Precision = precision.ToArray(),
                ColorSpace = image.ColorSpace, HasIcc = image.HasIcc,
            };
        }
        // channel definitions: color channels in association order, opacity last
        if (definitions != null)
        {
            var color = definitions.Where(d => d.Type == 0 && d.Channel < image.Channels.Length).OrderBy(d => d.Association).ToList();
            var opacity = definitions.FirstOrDefault(d => d.Type is 1 or 2 && d.Channel < image.Channels.Length);
            if (color.Count > 0)
            {
                var order = color.Select(d => d.Channel).ToList();
                if (definitions.Any(d => d.Type is 1 or 2 && d.Channel < image.Channels.Length)) order.Add(opacity.Channel);
                image.Channels = order.Select(i => image.Channels[i]).ToArray();
                image.Precision = order.Select(i => image.Precision[i]).ToArray();
                if (order.Count > color.Count) image.AlphaChannel = order.Count - 1;
            }
        }
        if (image.ColorSpace == 18 && image.ColorChannels >= 3) ConvertSycc(image);
        return image;
    }

    private static JpxImage? TryRawCodestream(byte[] data)
    {
        for (int i = 0; i + 1 < data.Length; i++)
            if (data[i] == 0xFF && data[i + 1] == 0x4F && i + 3 < data.Length && data[i + 2] == 0xFF && data[i + 3] == 0x51)
                return new Codestream(data, i, data.Length).Decode();
        return null;
    }

    private static void ConvertSycc(JpxImage image)
    {
        var (y, cb, cr) = (image.Channels[0], image.Channels[1], image.Channels[2]);
        int max = (1 << image.Precision[0]) - 1, offset = 1 << (image.Precision[0] - 1);
        for (int i = 0; i < y.Length; i++)
        {
            double yy = y[i], u = cb[i] - offset, v = cr[i] - offset;
            y[i] = Math.Clamp((int)Math.Round(yy + 1.402 * v), 0, max);
            cb[i] = Math.Clamp((int)Math.Round(yy - 0.344136 * u - 0.714136 * v), 0, max);
            cr[i] = Math.Clamp((int)Math.Round(yy + 1.772 * u), 0, max);
        }
    }

    // ------------------------------------------------------------------ codestream

    private sealed class ComponentInfo
    {
        public int Precision, XRsiz, YRsiz;
        public bool Signed;
    }

    private sealed class CodingStyle
    {
        public bool Precincts, Sop, Eph;
        public int Levels, CodeBlockWidthExp, CodeBlockHeightExp, BlockStyle;
        public bool Reversible;
        public int[] PrecinctWidthExp = Array.Empty<int>(), PrecinctHeightExp = Array.Empty<int>();

        public CodingStyle Clone() => (CodingStyle)MemberwiseClone();
    }

    private sealed class Quantization
    {
        public int Style, GuardBits;
        public int[] Exponent = Array.Empty<int>(), Mantissa = Array.Empty<int>();

        public Quantization Clone() => (Quantization)MemberwiseClone();
    }

    private readonly record struct ProgressionChange(int ResolutionStart, int ComponentStart, int LayerEnd, int ResolutionEnd, int ComponentEnd, int Order);

    private const int Bypass = 1, Reset = 2, TermAll = 4, VerticalCausal = 8, SegmentationSymbols = 32;

    private sealed class Codestream
    {
        private readonly byte[] _d;
        private readonly int _end;
        private int _width, _height, _x0, _y0, _tileWidth, _tileHeight, _tileX0, _tileY0;
        private ComponentInfo[] _components = Array.Empty<ComponentInfo>();
        private int _progression, _layers;
        private bool _mct;
        private CodingStyle _style = new();
        private CodingStyle?[] _componentStyles = Array.Empty<CodingStyle?>();
        private Quantization _quantization = new();
        private Quantization?[] _componentQuantization = Array.Empty<Quantization?>();
        private int[] _roiShift = Array.Empty<int>();
        private List<ProgressionChange>? _progressionChanges;
        private readonly List<(int Index, byte[] Data)> _packedHeaders = new(); // PPM
        private readonly List<int> _tilePartOrder = new();

        public Codestream(byte[] data, int start, int end)
        {
            _d = data;
            _end = Math.Min(end, data.Length);
            _p = start;
        }

        private int _p;

        private int U16(int p) => BinaryPrimitives.ReadUInt16BigEndian(_d.AsSpan(p));
        private int U32(int p) => (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(_d.AsSpan(p)), int.MaxValue);

        private sealed class TilePart
        {
            public int Tile;
            public List<(int Start, int End)> Data = new();
            public CodingStyle Style = null!;
            public CodingStyle?[] ComponentStyles = null!;
            public Quantization Quantization = null!;
            public Quantization?[] ComponentQuantization = null!;
            public int[] RoiShift = null!;
            public int Progression, Layers;
            public bool Mct;
            public List<ProgressionChange>? ProgressionChanges;
            public bool OwnProgressionChanges;
            public List<(int Index, byte[] Data)> PackedHeaders = new();
        }

        public JpxImage Decode()
        {
            if (U16(_p) != 0xFF4F) throw new PdfException("Missing JPEG 2000 SOC marker.");
            _p += 2;
            var tiles = new Dictionary<int, TilePart>();
            bool mainHeader = true;
            TilePart? current = null;
            while (_p + 2 <= _end)
            {
                int marker = U16(_p);
                if (marker == 0xFFD9) break; // EOC
                if (marker < 0xFF30) throw new PdfException("Invalid JPEG 2000 marker.");
                int length = marker is >= 0xFF30 and <= 0xFF3F ? 0 : U16(_p + 2);
                int segment = _p + 4;
                switch (marker)
                {
                    case 0xFF51:
                        ReadSiz(segment);
                        break;
                    case 0xFF52:
                        if (current == null) ReadCod(segment, ref _style, ref _progression, ref _layers, ref _mct, _componentStyles);
                        else ReadCod(segment, ref current.Style, ref current.Progression, ref current.Layers, ref current.Mct, current.ComponentStyles);
                        break;
                    case 0xFF53:
                        ReadCoc(segment, current?.Style ?? _style, current?.ComponentStyles ?? _componentStyles);
                        break;
                    case 0xFF5C:
                        if (current == null)
                        {
                            _quantization = ReadQuantization(segment, segment + length - 2);
                            Array.Clear(_componentQuantization);
                        }
                        else
                        {
                            current.Quantization = ReadQuantization(segment, segment + length - 2);
                            Array.Clear(current.ComponentQuantization);
                        }
                        break;
                    case 0xFF5D:
                    {
                        int c = _components.Length < 257 ? _d[segment] : U16(segment);
                        int q = segment + (_components.Length < 257 ? 1 : 2);
                        (current?.ComponentQuantization ?? _componentQuantization)[c] = ReadQuantization(q, segment + length - 2);
                        break;
                    }
                    case 0xFF5E:
                    {
                        int c = _components.Length < 257 ? _d[segment] : U16(segment);
                        int q = segment + (_components.Length < 257 ? 1 : 2);
                        (current?.RoiShift ?? _roiShift)[c] = _d[q + 1];
                        break;
                    }
                    case 0xFF5F:
                    {
                        var list = new List<ProgressionChange>();
                        int size = _components.Length < 257 ? 1 : 2;
                        for (int q = segment; q + 5 + 2 * size <= segment + length - 2; q += 5 + 2 * size)
                        {
                            int cs = size == 1 ? _d[q + 1] : U16(q + 1);
                            int lye = U16(q + 1 + size);
                            int re = _d[q + 3 + size];
                            int ce = size == 1 ? _d[q + 4 + size] : U16(q + 4 + size);
                            if (ce == 0) ce = 256;
                            list.Add(new ProgressionChange(_d[q], cs, lye, re, ce, _d[q + 4 + 2 * size]));
                        }
                        if (current == null) _progressionChanges = list;
                        else if (current.OwnProgressionChanges) current.ProgressionChanges!.AddRange(list);
                        else
                        {
                            current.ProgressionChanges = list;
                            current.OwnProgressionChanges = true;
                        }
                        break;
                    }
                    case 0xFF60: // PPM
                        _packedHeaders.Add((_d[segment], _d.AsSpan(segment + 1, length - 3).ToArray()));
                        break;
                    case 0xFF61: // PPT
                        current?.PackedHeaders.Add((_d[segment], _d.AsSpan(segment + 1, length - 3).ToArray()));
                        break;
                    case 0xFF90: // SOT
                    {
                        mainHeader = false;
                        int tile = U16(segment);
                        int partLength = U32(segment + 2);
                        if (!tiles.TryGetValue(tile, out current))
                        {
                            current = new TilePart
                            {
                                Tile = tile, Style = _style.Clone(), ComponentStyles = _componentStyles.Select(s => s?.Clone()).ToArray(),
                                Quantization = _quantization.Clone(), ComponentQuantization = _componentQuantization.ToArray(),
                                RoiShift = _roiShift.ToArray(), Progression = _progression, Layers = _layers, Mct = _mct,
                                ProgressionChanges = _progressionChanges,
                            };
                            tiles[tile] = current;
                        }
                        int partEnd = partLength == 0 ? _end : Math.Min(_end, _p + partLength);
                        _tilePartOrder.Add(tile);
                        // tile-part header up to SOD, then the data
                        _p = segment + length - 2;
                        int firstPpt = current.PackedHeaders.Count;
                        while (_p + 4 <= partEnd && U16(_p) != 0xFF93)
                        {
                            int m = U16(_p), l = U16(_p + 2);
                            int s = _p + 4;
                            switch (m)
                            {
                                case 0xFF52:
                                    ReadCod(s, ref current.Style, ref current.Progression, ref current.Layers, ref current.Mct, current.ComponentStyles);
                                    Array.Clear(current.ComponentStyles); // a tile COD takes precedence over main COC
                                    break;
                                case 0xFF53:
                                    ReadCoc(s, current.Style, current.ComponentStyles);
                                    break;
                                case 0xFF5C:
                                    current.Quantization = ReadQuantization(s, s + l - 2);
                                    Array.Clear(current.ComponentQuantization);
                                    break;
                                case 0xFF5D:
                                {
                                    int c = _components.Length < 257 ? _d[s] : U16(s);
                                    current.ComponentQuantization[c] = ReadQuantization(s + (_components.Length < 257 ? 1 : 2), s + l - 2);
                                    break;
                                }
                                case 0xFF5E:
                                {
                                    int c = _components.Length < 257 ? _d[s] : U16(s);
                                    current.RoiShift[c] = _d[s + (_components.Length < 257 ? 1 : 2) + 1];
                                    break;
                                }
                                case 0xFF5F:
                                {
                                    _p = s - 4;
                                    var list = new List<ProgressionChange>();
                                    int size = _components.Length < 257 ? 1 : 2;
                                    for (int q = s; q + 5 + 2 * size <= s + l - 2; q += 5 + 2 * size)
                                    {
                                        int cs = size == 1 ? _d[q + 1] : U16(q + 1);
                                        int re = _d[q + 3 + size];
                                        int ce = size == 1 ? _d[q + 4 + size] : U16(q + 4 + size);
                                        if (ce == 0) ce = 256;
                                        list.Add(new ProgressionChange(_d[q], cs, U16(q + 1 + size), re, ce, _d[q + 4 + 2 * size]));
                                    }
                                    // the first tile POC replaces the main header's; later tile-parts add to it
                                    if (current.OwnProgressionChanges) current.ProgressionChanges!.AddRange(list);
                                    else current.ProgressionChanges = list;
                                    current.OwnProgressionChanges = true;
                                    break;
                                }
                                case 0xFF61:
                                    current.PackedHeaders.Add((_d[s], _d.AsSpan(s + 1, l - 3).ToArray()));
                                    break;
                            }
                            _p = s + l - 2;
                        }
                        // PPT segments of a tile-part are concatenated in the order of their index (Zppt)
                        if (current.PackedHeaders.Count - firstPpt > 1)
                        {
                            var sorted = current.PackedHeaders.Skip(firstPpt).OrderBy(h => h.Index).ToList();
                            current.PackedHeaders.RemoveRange(firstPpt, sorted.Count);
                            current.PackedHeaders.AddRange(sorted);
                        }
                        _p += 2; // SOD
                        if (_p < partEnd) current.Data.Add((_p, partEnd));
                        _p = partEnd;
                        continue;
                    }
                }
                _p = segment + length - 2;
                if (length < 2 && marker is not (>= 0xFF30 and <= 0xFF3F)) break;
            }
            _ = mainHeader;
            if (_components.Length == 0) throw new PdfException("JPEG 2000 codestream without SIZ marker.");

            int width = _width - _x0, height = _height - _y0;
            PdfLimits.Current.CheckImage(width, height);
            var output = new int[_components.Length][];
            var outputWidth = new int[_components.Length];
            for (int c = 0; c < _components.Length; c++)
            {
                int cw = Ceil(_width, _components[c].XRsiz) - Ceil(_x0, _components[c].XRsiz);
                int ch = Ceil(_height, _components[c].YRsiz) - Ceil(_y0, _components[c].YRsiz);
                PdfLimits.Current.CheckDecodedSize((long)cw * ch * 4);
                output[c] = new int[(long)cw * ch];
                outputWidth[c] = cw;
            }

            // PPM: the packet headers of the tile-parts, in the order of the tile-parts in the codestream
            if (_packedHeaders.Count > 0)
            {
                int index = 0;
                foreach (var headers in SplitPpm())
                {
                    if (index < _tilePartOrder.Count && tiles.TryGetValue(_tilePartOrder[index], out var owner))
                        owner.PackedHeaders.Add((owner.PackedHeaders.Count, headers));
                    index++;
                }
            }
            foreach (var part in tiles.Values.OrderBy(t => t.Tile)) DecodeTile(part, output, outputWidth);

            // full image size per channel (upsampling subsampled components)
            var channels = new int[_components.Length][];
            for (int c = 0; c < _components.Length; c++)
            {
                var info = _components[c];
                if (info.XRsiz == 1 && info.YRsiz == 1)
                {
                    channels[c] = output[c];
                    continue;
                }
                var full = new int[(long)width * height];
                int cx0 = Ceil(_x0, info.XRsiz), cy0 = Ceil(_y0, info.YRsiz), cw = outputWidth[c];
                int ch = cw == 0 ? 0 : output[c].Length / cw;
                for (int y = 0; y < height; y++)
                {
                    int sy = Math.Clamp((y + _y0) / info.YRsiz - cy0, 0, Math.Max(0, ch - 1));
                    for (int x = 0; x < width; x++)
                    {
                        int sx = Math.Clamp((x + _x0) / info.XRsiz - cx0, 0, Math.Max(0, cw - 1));
                        full[(long)y * width + x] = ch == 0 ? 0 : output[c][(long)sy * cw + sx];
                    }
                }
                channels[c] = full;
            }
            return new JpxImage { Width = width, Height = height, Channels = channels, Precision = _components.Select(c => c.Precision).ToArray() };
        }

        /// <summary>PPM marker data: Nppm (4 bytes) followed by the packet headers of a tile, repeated.</summary>
        private IEnumerable<byte[]> SplitPpm()
        {
            var all = _packedHeaders.OrderBy(h => h.Index).SelectMany(h => h.Data).ToArray();
            // the first byte of each PPM segment was its index (Zppm), already removed
            int p = 0;
            while (p + 4 <= all.Length)
            {
                int n = (int)BinaryPrimitives.ReadUInt32BigEndian(all.AsSpan(p));
                p += 4;
                n = Math.Min(n, all.Length - p);
                yield return all.AsSpan(p, n).ToArray();
                p += n;
            }
        }

        private void ReadSiz(int p)
        {
            _width = U32(p + 2);
            _height = U32(p + 6);
            _x0 = U32(p + 10);
            _y0 = U32(p + 14);
            _tileWidth = U32(p + 18);
            _tileHeight = U32(p + 22);
            _tileX0 = U32(p + 26);
            _tileY0 = U32(p + 30);
            int count = U16(p + 34);
            if (count <= 0 || count > 16384 || _tileWidth <= 0 || _tileHeight <= 0 || _width <= _x0 || _height <= _y0
                || _tileX0 > _x0 || _tileY0 > _y0 || (long)_tileX0 + _tileWidth <= _x0 || (long)_tileY0 + _tileHeight <= _y0)
                throw new PdfException("Invalid JPEG 2000 image size.");
            _components = new ComponentInfo[count];
            for (int c = 0; c < count; c++)
            {
                int q = p + 36 + 3 * c;
                _components[c] = new ComponentInfo
                {
                    Precision = (_d[q] & 0x7F) + 1, Signed = (_d[q] & 0x80) != 0,
                    XRsiz = Math.Max(1, (int)_d[q + 1]), YRsiz = Math.Max(1, (int)_d[q + 2]),
                };
                if (_components[c].Precision > 31) throw new PdfException("Unsupported JPEG 2000 precision.");
            }
            _componentStyles = new CodingStyle?[count];
            _componentQuantization = new Quantization?[count];
            _roiShift = new int[count];
        }

        private void ReadCod(int p, ref CodingStyle style, ref int progression, ref int layers, ref bool mct, CodingStyle?[] componentStyles)
        {
            int scod = _d[p];
            progression = _d[p + 1];
            layers = U16(p + 2);
            mct = _d[p + 4] != 0;
            style = ReadStyle(p + 5, (scod & 1) != 0);
            style.Sop = (scod & 2) != 0;
            style.Eph = (scod & 4) != 0;
            // a COD resets component styles that came from the same header level only for components without COC
            for (int c = 0; c < componentStyles.Length; c++)
                if (componentStyles[c] is { } cs)
                {
                    cs.Sop = style.Sop;
                    cs.Eph = style.Eph;
                }
        }

        private void ReadCoc(int p, CodingStyle main, CodingStyle?[] componentStyles)
        {
            int c = _components.Length < 257 ? _d[p] : U16(p);
            int q = p + (_components.Length < 257 ? 1 : 2);
            var style = ReadStyle(q + 1, (_d[q] & 1) != 0);
            style.Sop = main.Sop;
            style.Eph = main.Eph;
            if (c < componentStyles.Length) componentStyles[c] = style;
        }

        private CodingStyle ReadStyle(int p, bool precincts)
        {
            var style = new CodingStyle
            {
                Precincts = precincts, Levels = _d[p], CodeBlockWidthExp = (_d[p + 1] & 0xF) + 2,
                CodeBlockHeightExp = (_d[p + 2] & 0xF) + 2, BlockStyle = _d[p + 3], Reversible = _d[p + 4] == 1,
            };
            if (style.Levels > 32 || style.CodeBlockWidthExp + style.CodeBlockHeightExp > 12)
                throw new PdfException("Invalid JPEG 2000 coding style.");
            style.PrecinctWidthExp = new int[style.Levels + 1];
            style.PrecinctHeightExp = new int[style.Levels + 1];
            for (int r = 0; r <= style.Levels; r++)
            {
                if (precincts)
                {
                    style.PrecinctWidthExp[r] = _d[p + 5 + r] & 0xF;
                    style.PrecinctHeightExp[r] = _d[p + 5 + r] >> 4;
                }
                else
                {
                    style.PrecinctWidthExp[r] = style.PrecinctHeightExp[r] = 15;
                }
            }
            return style;
        }

        private Quantization ReadQuantization(int p, int end)
        {
            var q = new Quantization { Style = _d[p] & 0x1F, GuardBits = _d[p] >> 5 };
            var exponent = new List<int>();
            var mantissa = new List<int>();
            if (q.Style == 0)
            {
                for (int i = p + 1; i < end; i++)
                {
                    exponent.Add(_d[i] >> 3);
                    mantissa.Add(0);
                }
            }
            else
            {
                for (int i = p + 1; i + 1 < end; i += 2)
                {
                    int v = U16(i);
                    exponent.Add(v >> 11);
                    mantissa.Add(v & 0x7FF);
                }
            }
            q.Exponent = exponent.ToArray();
            q.Mantissa = mantissa.ToArray();
            return q;
        }

        // -------------------------------------------------------------- tiles

        private sealed class Subband
        {
            public int Orientation; // 0 LL, 1 HL, 2 LH, 3 HH
            public int X0, Y0, X1, Y1, Level;
            public float[]? Float;
            public int[]? Int;
            public double Step;
            public int MagnitudeBits; // Mb
            public int Width => X1 - X0;
            public int Height => Y1 - Y0;
        }

        private sealed class CodeBlock
        {
            public int X0, Y0, X1, Y1;
            public bool Included;
            public int Lblock = 3, ZeroBitplanes, Passes;
            public List<Segment> Segments = new();
        }

        private sealed class Segment
        {
            public List<(int Start, int Length)> Chunks = new();
            public int Passes, MaxPasses;
        }

        private sealed class PrecinctBand
        {
            public required Subband Band;
            public required CodeBlock[] Blocks;
            public int Columns, Rows;
            public required TagTree Inclusion, ZeroBitplanes;
        }

        private sealed class Precinct
        {
            public List<PrecinctBand> Bands = new();
            public int RefX, RefY; // position on the reference grid (for position-driven progressions)
        }

        private sealed class Resolution
        {
            public int X0, Y0, X1, Y1;
            public List<Subband> Bands = new();
            public Precinct[] Precincts = Array.Empty<Precinct>();
        }

        private sealed class TileComponent
        {
            public int X0, Y0, X1, Y1;
            public required CodingStyle Style;
            public required Quantization Quantization;
            public Resolution[] Resolutions = Array.Empty<Resolution>();
            public int RoiShift;
        }

        private static int Ceil(long a, long b) => (int)((a + b - 1) / b);

        private static int CeilShift(int a, int shift) => (int)(((long)a + (1L << shift) - 1) >> shift);

        private void DecodeTile(TilePart part, int[][] output, int[] outputWidth)
        {
            int columns = Ceil(_width - _tileX0, _tileWidth);
            int p = part.Tile % columns, q = part.Tile / columns;
            int tx0 = Math.Max(_tileX0 + p * _tileWidth, _x0), ty0 = Math.Max(_tileY0 + q * _tileHeight, _y0);
            int tx1 = Math.Min(_tileX0 + (p + 1) * _tileWidth, _width), ty1 = Math.Min(_tileY0 + (q + 1) * _tileHeight, _height);
            if (tx1 <= tx0 || ty1 <= ty0) return;

            var components = new TileComponent[_components.Length];
            for (int c = 0; c < components.Length; c++)
            {
                var info = _components[c];
                var style = part.ComponentStyles[c] ?? part.Style;
                var quant = part.ComponentQuantization[c] ?? part.Quantization;
                var tc = new TileComponent
                {
                    X0 = Ceil(tx0, info.XRsiz), Y0 = Ceil(ty0, info.YRsiz), X1 = Ceil(tx1, info.XRsiz), Y1 = Ceil(ty1, info.YRsiz),
                    Style = style, Quantization = quant, RoiShift = part.RoiShift[c],
                };
                BuildResolutions(tc, info, tx0, ty0);
                components[c] = tc;
            }

            // packets
            var data = new List<byte>();
            foreach (var (start, end) in part.Data) data.AddRange(_d.AsSpan(start, end - start).ToArray());
            var body = data.ToArray();
            // PPT segments by their index within each tile-part; PPM headers in tile-part order
            byte[]? headers = part.PackedHeaders.Count > 0 ? part.PackedHeaders.SelectMany(h => h.Data).ToArray() : null;
            var reader = new PacketReader(body, headers);
            foreach (var (layer, resolution, component, precinct) in Packets(part, components, tx0, ty0))
            {
                if (reader.AtEnd) break;
                var tc = components[component];
                if (resolution >= tc.Resolutions.Length) continue;
                var res = tc.Resolutions[resolution];
                if (precinct >= res.Precincts.Length) continue;
                ReadPacket(reader, res.Precincts[precinct], layer, tc.Style);
            }

            // code-blocks, inverse transforms
            var samples = new float[components.Length][];
            var intSamples = new int[components.Length][];
            for (int c = 0; c < components.Length; c++)
            {
                var tc = components[c];
                foreach (var res in tc.Resolutions)
                    foreach (var precinct in res.Precincts)
                        foreach (var pb in precinct.Bands)
                            foreach (var block in pb.Blocks)
                                DecodeCodeBlock(block, pb.Band, tc, body);
                if (tc.Style.Reversible) intSamples[c] = InverseTransformInt(tc);
                else samples[c] = InverseTransformFloat(tc);
            }

            // component transform
            if (part.Mct && components.Length >= 3)
            {
                if (components[0].Style.Reversible)
                {
                    var (y0, y1, y2) = (intSamples[0], intSamples[1], intSamples[2]);
                    if (y0 != null && y1 != null && y2 != null && y0.Length == y1.Length && y1.Length == y2.Length)
                        for (int i = 0; i < y0.Length; i++)
                        {
                            int g = y0[i] - ((y2[i] + y1[i]) >> 2);
                            int r = y2[i] + g, b = y1[i] + g;
                            y0[i] = r;
                            y1[i] = g;
                            y2[i] = b;
                        }
                }
                else
                {
                    var (y0, y1, y2) = (samples[0], samples[1], samples[2]);
                    if (y0 != null && y1 != null && y2 != null && y0.Length == y1.Length && y1.Length == y2.Length)
                        for (int i = 0; i < y0.Length; i++)
                        {
                            float y = y0[i], u = y1[i], v = y2[i];
                            y0[i] = y + 1.402f * v;
                            y1[i] = y - 0.34413f * u - 0.71414f * v;
                            y2[i] = y + 1.772f * u;
                        }
                }
            }

            // level shift, clamping, output
            for (int c = 0; c < components.Length; c++)
            {
                var tc = components[c];
                var info = _components[c];
                int w = tc.X1 - tc.X0, h = tc.Y1 - tc.Y0;
                int shift = info.Signed ? 0 : 1 << (info.Precision - 1);
                int min = info.Signed ? -(1 << (info.Precision - 1)) : 0, max = info.Signed ? (1 << (info.Precision - 1)) - 1 : (1 << info.Precision) - 1;
                int ox = tc.X0 - Ceil(_x0, info.XRsiz), oy = tc.Y0 - Ceil(_y0, info.YRsiz);
                int ow = outputWidth[c];
                for (int y = 0; y < h; y++)
                {
                    long target = (long)(oy + y) * ow + ox;
                    for (int x = 0; x < w; x++)
                    {
                        int v = tc.Style.Reversible ? intSamples[c][y * w + x] : (int)Math.Round(samples[c][y * w + x]);
                        v = Math.Clamp(v + shift, min, max);
                        // signed samples are offset to unsigned for the output
                        output[c][target + x] = info.Signed ? v - min : v;
                    }
                }
            }
        }

        private void BuildResolutions(TileComponent tc, ComponentInfo info, int tx0, int ty0)
        {
            var style = tc.Style;
            int levels = style.Levels;
            tc.Resolutions = new Resolution[levels + 1];
            int bandIndex = 0;
            for (int r = 0; r <= levels; r++)
            {
                int scale = levels - r;
                var res = new Resolution
                {
                    X0 = CeilShift(tc.X0, scale), Y0 = CeilShift(tc.Y0, scale), X1 = CeilShift(tc.X1, scale), Y1 = CeilShift(tc.Y1, scale),
                };
                tc.Resolutions[r] = res;
                if (r == 0)
                {
                    res.Bands.Add(MakeBand(tc, 0, levels, bandIndex++, info));
                }
                else
                {
                    int nb = levels - r + 1;
                    for (int o = 1; o <= 3; o++) res.Bands.Add(MakeBand(tc, o, nb, bandIndex++, info));
                }
                // precincts and code-blocks
                int ppx = style.PrecinctWidthExp[r], ppy = style.PrecinctHeightExp[r];
                // (OpenJPEG writes precinct exponents of 0 above resolution 0: such precincts are at most one subband
                // sample wide, any non-negative code-block exponent gives the same blocks)
                int cbw = Math.Max(0, Math.Min(style.CodeBlockWidthExp, r == 0 ? ppx : ppx - 1));
                int cbh = Math.Max(0, Math.Min(style.CodeBlockHeightExp, r == 0 ? ppy : ppy - 1));
                int pw = res.X1 > res.X0 ? CeilShift(res.X1, ppx) - (res.X0 >> ppx) : 0;
                int ph = res.Y1 > res.Y0 ? CeilShift(res.Y1, ppy) - (res.Y0 >> ppy) : 0;
                if ((long)pw * ph > 1 << 20) throw new PdfException("Too many JPEG 2000 precincts.");
                res.Precincts = new Precinct[pw * ph];
                for (int py = 0; py < ph; py++)
                    for (int px = 0; px < pw; px++)
                    {
                        var precinct = new Precinct();
                        // precinct area in resolution coordinates
                        int rx0 = Math.Max(res.X0, ((res.X0 >> ppx) + px) << ppx), ry0 = Math.Max(res.Y0, ((res.Y0 >> ppy) + py) << ppy);
                        int rx1 = Math.Min(res.X1, ((res.X0 >> ppx) + px + 1) << ppx), ry1 = Math.Min(res.Y1, ((res.Y0 >> ppy) + py + 1) << ppy);
                        // emission position of position-driven progressions (B.12.1.3): the start of the precinct on the
                        // reference grid, or the tile origin for a precinct cut by the tile
                        int ux = ((res.X0 >> ppx) + px) << ppx, uy = ((res.Y0 >> ppy) + py) << ppy;
                        precinct.RefX = ux < res.X0 ? tx0 : (int)Math.Min(int.MaxValue, ((long)ux << scale) * info.XRsiz);
                        precinct.RefY = uy < res.Y0 ? ty0 : (int)Math.Min(int.MaxValue, ((long)uy << scale) * info.YRsiz);
                        foreach (var band in res.Bands)
                        {
                            // the precinct in the subband (half size above resolution 0)
                            int bx0 = r == 0 ? rx0 : CeilShift(rx0, 1), by0 = r == 0 ? ry0 : CeilShift(ry0, 1);
                            int bx1 = r == 0 ? rx1 : CeilShift(rx1, 1), by1 = r == 0 ? ry1 : CeilShift(ry1, 1);
                            if (r > 0)
                            {
                                // subband coordinates of the precinct: depends on the band's offset
                                int ox = band.Orientation is 1 or 3 ? 1 : 0, oy = band.Orientation is 2 or 3 ? 1 : 0;
                                bx0 = Math.Max(band.X0, Ceil(Math.Max(0, rx0 - ox), 2));
                                by0 = Math.Max(band.Y0, Ceil(Math.Max(0, ry0 - oy), 2));
                                bx1 = Math.Min(band.X1, Ceil(Math.Max(0, rx1 - ox), 2));
                                by1 = Math.Min(band.Y1, Ceil(Math.Max(0, ry1 - oy), 2));
                            }
                            else
                            {
                                bx0 = Math.Max(band.X0, bx0);
                                by0 = Math.Max(band.Y0, by0);
                                bx1 = Math.Min(band.X1, bx1);
                                by1 = Math.Min(band.Y1, by1);
                            }
                            int cols = bx1 > bx0 ? CeilShift(bx1, cbw) - (bx0 >> cbw) : 0;
                            int rows = by1 > by0 ? CeilShift(by1, cbh) - (by0 >> cbh) : 0;
                            var blocks = new CodeBlock[cols * rows];
                            for (int j = 0; j < rows; j++)
                                for (int i = 0; i < cols; i++)
                                {
                                    int x0 = ((bx0 >> cbw) + i) << cbw, y0 = ((by0 >> cbh) + j) << cbh;
                                    blocks[j * cols + i] = new CodeBlock
                                    {
                                        X0 = Math.Max(bx0, x0), Y0 = Math.Max(by0, y0),
                                        X1 = Math.Min(bx1, x0 + (1 << cbw)), Y1 = Math.Min(by1, y0 + (1 << cbh)),
                                    };
                                }
                            precinct.Bands.Add(new PrecinctBand
                            {
                                Band = band, Blocks = blocks, Columns = cols, Rows = rows,
                                Inclusion = new TagTree(cols, rows), ZeroBitplanes = new TagTree(cols, rows),
                            });
                        }
                        res.Precincts[py * pw + px] = precinct;
                    }
            }
        }

        private Subband MakeBand(TileComponent tc, int orientation, int nb, int index, ComponentInfo info)
        {
            int ox = orientation is 1 or 3 ? 1 : 0, oy = orientation is 2 or 3 ? 1 : 0;
            long half = nb > 0 ? 1L << (nb - 1) : 0;
            var band = new Subband
            {
                Orientation = orientation, Level = nb,
                X0 = (int)Ceil(Math.Max(0, tc.X0 - half * ox), 1L << nb), Y0 = (int)Ceil(Math.Max(0, tc.Y0 - half * oy), 1L << nb),
                X1 = (int)Ceil(Math.Max(0, tc.X1 - half * ox), 1L << nb), Y1 = (int)Ceil(Math.Max(0, tc.Y1 - half * oy), 1L << nb),
            };
            var q = tc.Quantization;
            int exponent, mantissa;
            if (q.Style == 1)
            {
                // derived: from the LL values
                exponent = (q.Exponent.Length > 0 ? q.Exponent[0] : 0) - (tc.Style.Levels - nb);
                mantissa = q.Mantissa.Length > 0 ? q.Mantissa[0] : 0;
            }
            else
            {
                int i = Math.Min(index, Math.Max(0, q.Exponent.Length - 1));
                exponent = q.Exponent.Length > 0 ? q.Exponent[i] : 0;
                mantissa = q.Mantissa.Length > 0 ? q.Mantissa[i] : 0;
            }
            band.MagnitudeBits = q.GuardBits + exponent - 1;
            int gain = orientation switch { 0 => 0, 3 => 2, _ => 1 };
            int dynamicRange = info.Precision + gain;
            band.Step = q.Style == 0 ? 1 : Math.Pow(2, dynamicRange - exponent) * (1 + mantissa / 2048.0);
            int area = Math.Max(0, band.Width) * Math.Max(0, band.Height);
            PdfLimits.Current.CheckDecodedSize((long)area * 4);
            if (tc.Style.Reversible) band.Int = new int[area];
            else band.Float = new float[area];
            return band;
        }

        // -------------------------------------------------------------- progression

        private IEnumerable<(int Layer, int Resolution, int Component, int Precinct)> Packets(TilePart part, TileComponent[] components,
            int tx0, int ty0)
        {
            int layers = part.Layers;
            int maxRes = components.Max(c => c.Resolutions.Length);
            var changes = part.ProgressionChanges ?? new List<ProgressionChange>
            {
                new(0, 0, layers, maxRes, components.Length, part.Progression),
            };
            var emitted = new HashSet<(int, int, int, int)>();
            foreach (var change in changes)
            {
                int le = Math.Min(change.LayerEnd, layers), rs = change.ResolutionStart, re = Math.Min(change.ResolutionEnd, maxRes);
                int cs = change.ComponentStart, ce = Math.Min(change.ComponentEnd, components.Length);
                foreach (var packet in Order(change.Order, components, le, rs, re, cs, ce))
                    if (part.ProgressionChanges == null || emitted.Add(packet))
                        yield return packet;
            }
            if (part.ProgressionChanges != null)
            {
                // packets not covered by the changes follow in the default order
                foreach (var packet in Order(part.Progression, components, layers, 0, maxRes, 0, components.Length))
                    if (emitted.Add(packet)) yield return packet;
            }
            _ = tx0;
            _ = ty0;
        }

        private static IEnumerable<(int, int, int, int)> Order(int order, TileComponent[] components, int layers, int rs, int re, int cs, int ce)
        {
            switch (order)
            {
                case 0: // LRCP
                    for (int l = 0; l < layers; l++)
                        for (int r = rs; r < re; r++)
                            for (int c = cs; c < ce; c++)
                                if (r < components[c].Resolutions.Length)
                                    for (int k = 0; k < components[c].Resolutions[r].Precincts.Length; k++)
                                        yield return (l, r, c, k);
                    break;
                case 1: // RLCP
                    for (int r = rs; r < re; r++)
                        for (int l = 0; l < layers; l++)
                            for (int c = cs; c < ce; c++)
                                if (r < components[c].Resolutions.Length)
                                    for (int k = 0; k < components[c].Resolutions[r].Precincts.Length; k++)
                                        yield return (l, r, c, k);
                    break;
                default:
                {
                    // position-driven orders: precincts sorted by their position on the reference grid
                    var list = new List<(int R, int C, int K, int X, int Y)>();
                    for (int c = cs; c < ce; c++)
                        for (int r = rs; r < Math.Min(re, components[c].Resolutions.Length); r++)
                        {
                            var precincts = components[c].Resolutions[r].Precincts;
                            for (int k = 0; k < precincts.Length; k++) list.Add((r, c, k, precincts[k].RefX, precincts[k].RefY));
                        }
                    IEnumerable<(int R, int C, int K, int X, int Y)> sorted = order switch
                    {
                        2 => list.OrderBy(e => e.R).ThenBy(e => e.Y).ThenBy(e => e.X).ThenBy(e => e.C),   // RPCL
                        3 => list.OrderBy(e => e.Y).ThenBy(e => e.X).ThenBy(e => e.C).ThenBy(e => e.R),   // PCRL
                        _ => list.OrderBy(e => e.C).ThenBy(e => e.Y).ThenBy(e => e.X).ThenBy(e => e.R),   // CPRL
                    };
                    foreach (var e in sorted)
                        for (int l = 0; l < layers; l++)
                            yield return (l, e.R, e.C, e.K);
                    break;
                }
            }
        }

        // -------------------------------------------------------------- tier 2: packets

        private sealed class PacketReader
        {
            private readonly byte[] _body;
            private readonly byte[]? _headers;
            private int _bodyPosition, _headerPosition;

            public PacketReader(byte[] body, byte[]? headers)
            {
                _body = body;
                _headers = headers;
            }

            public bool AtEnd => _headers == null ? _bodyPosition >= _body.Length : _headerPosition >= _headers.Length && _bodyPosition >= _body.Length;

            private byte[] HeaderData => _headers ?? _body;

            private int HeaderPosition
            {
                get => _headers == null ? _bodyPosition : _headerPosition;
                set
                {
                    if (_headers == null) _bodyPosition = value;
                    else _headerPosition = value;
                }
            }

            private int _bitBuffer, _bitCount;
            private bool _lastWasFF;

            public void SkipSop()
            {
                if (_bodyPosition + 6 <= _body.Length && _body[_bodyPosition] == 0xFF && _body[_bodyPosition + 1] == 0x91) _bodyPosition += 6;
            }

            public void BeginHeader()
            {
                _bitCount = 0;
                _lastWasFF = false;
            }

            public int ReadBit()
            {
                if (_bitCount == 0)
                {
                    var data = HeaderData;
                    int p = HeaderPosition;
                    int b = p < data.Length ? data[p] : 0;
                    HeaderPosition = p + 1;
                    _bitCount = _lastWasFF ? 7 : 8;
                    _bitBuffer = b;
                    _lastWasFF = b == 0xFF;
                }
                _bitCount--;
                return (_bitBuffer >> _bitCount) & 1;
            }

            public int ReadBits(int n)
            {
                int v = 0;
                for (int i = 0; i < n; i++) v = (v << 1) | ReadBit();
                return v;
            }

            public void EndHeader(bool eph)
            {
                // the header ends byte-aligned; after 0xFF a stuffed byte follows
                if (_lastWasFF) HeaderPosition++;
                _bitCount = 0;
                _lastWasFF = false;
                if (eph)
                {
                    var data = HeaderData;
                    int p = HeaderPosition;
                    if (p + 1 < data.Length && data[p] == 0xFF && data[p + 1] == 0x92) HeaderPosition = p + 2;
                }
            }

            /// <summary>Takes <paramref name="length"/> bytes of code-block data (start in the body).</summary>
            public int TakeBody(int length)
            {
                if (length < 0) throw new PdfException("Invalid JPEG 2000 packet header.");
                int start = _bodyPosition;
                _bodyPosition = Math.Min(_body.Length, _bodyPosition + length);
                return start;
            }

            public byte[] Body => _body;
        }

        private void ReadPacket(PacketReader reader, Precinct precinct, int layer, CodingStyle style)
        {
            if (style.Sop) reader.SkipSop();
            reader.BeginHeader();
            var contributions = new List<(CodeBlock Block, List<(Segment Segment, int Length)> Parts)>();
            if (reader.ReadBit() != 0)
            {
                foreach (var pb in precinct.Bands)
                {
                    for (int j = 0; j < pb.Rows; j++)
                        for (int i = 0; i < pb.Columns; i++)
                        {
                            var block = pb.Blocks[j * pb.Columns + i];
                            bool included;
                            bool first = !block.Included;
                            if (first) included = pb.Inclusion.Decode(reader, i, j, layer + 1);
                            else included = reader.ReadBit() != 0;
                            if (!included) continue;
                            if (first)
                            {
                                int threshold = 1;
                                while (!pb.ZeroBitplanes.Decode(reader, i, j, threshold))
                                    if (++threshold > 64) throw new PdfException("Invalid JPEG 2000 packet header.");
                                block.ZeroBitplanes = pb.ZeroBitplanes.Value(i, j);
                                block.Included = true;
                            }
                            int passes = ReadPassCount(reader);
                            while (reader.ReadBit() != 0)
                                if (++block.Lblock > 24) throw new PdfException("Invalid JPEG 2000 packet header.");
                            // the passes are split into codeword segments
                            var parts = new List<(Segment, int)>();
                            int remaining = passes;
                            while (remaining > 0)
                            {
                                var segment = block.Segments.Count > 0 && block.Segments[^1].Passes < block.Segments[^1].MaxPasses
                                    ? block.Segments[^1] : NewSegment(block, style.BlockStyle);
                                int take = Math.Min(remaining, segment.MaxPasses - segment.Passes);
                                int bits = block.Lblock + FloorLog2(take);
                                int length = reader.ReadBits(bits);
                                segment.Passes += take;
                                block.Passes += take;
                                remaining -= take;
                                parts.Add((segment, length));
                            }
                            contributions.Add((block, parts));
                        }
                }
            }
            reader.EndHeader(style.Eph);
            foreach (var (_, parts) in contributions)
                foreach (var (segment, length) in parts)
                    segment.Chunks.Add((reader.TakeBody(length), length));
        }

        private static Segment NewSegment(CodeBlock block, int blockStyle)
        {
            int index = block.Segments.Count;
            int max;
            if ((blockStyle & TermAll) != 0) max = 1;
            else if ((blockStyle & Bypass) != 0) max = index == 0 ? 10 : index % 2 == 1 ? 2 : 1;
            else max = int.MaxValue;
            var segment = new Segment { MaxPasses = max };
            block.Segments.Add(segment);
            return segment;
        }

        private static int FloorLog2(int n)
        {
            int r = 0;
            while ((n >>= 1) > 0) r++;
            return r;
        }

        private static int ReadPassCount(PacketReader reader)
        {
            if (reader.ReadBit() == 0) return 1;
            if (reader.ReadBit() == 0) return 2;
            int v = reader.ReadBits(2);
            if (v < 3) return 3 + v;
            v = reader.ReadBits(5);
            if (v < 31) return 6 + v;
            return 37 + reader.ReadBits(7);
        }

        private sealed class TagTree
        {
            private readonly int[][] _value, _low;
            private readonly int[] _width;

            public TagTree(int width, int height)
            {
                var widths = new List<int>();
                var levels = new List<(int, int)>();
                do
                {
                    levels.Add((width, height));
                    widths.Add(width);
                    width = (width + 1) / 2;
                    height = (height + 1) / 2;
                } while (levels[^1].Item1 > 1 || levels[^1].Item2 > 1);
                _value = levels.Select(l => Enumerable.Repeat(int.MaxValue, Math.Max(1, l.Item1 * l.Item2)).ToArray()).ToArray();
                _low = levels.Select(l => new int[Math.Max(1, l.Item1 * l.Item2)]).ToArray();
                _width = widths.ToArray();
            }

            public bool Decode(PacketReader reader, int x, int y, int threshold)
            {
                int low = 0;
                for (int level = _value.Length - 1; level >= 0; level--)
                {
                    int index = (y >> level) * _width[level] + (x >> level);
                    ref int nodeLow = ref _low[level][index];
                    ref int value = ref _value[level][index];
                    if (low > nodeLow) nodeLow = low;
                    else low = nodeLow;
                    while (low < threshold && low < value)
                    {
                        if (reader.ReadBit() != 0) value = low;
                        else low++;
                    }
                    nodeLow = low;
                }
                return _value[0][y * _width[0] + x] < threshold;
            }

            public int Value(int x, int y) => _value[0][y * _width[0] + x];
        }

        // -------------------------------------------------------------- tier 1: code-blocks

        private static void DecodeCodeBlock(CodeBlock block, Subband band, TileComponent tc, byte[] body)
        {
            int w = block.X1 - block.X0, h = block.Y1 - block.Y0;
            if (w <= 0 || h <= 0 || block.Passes == 0) return;
            int planes = band.MagnitudeBits + tc.RoiShift - block.ZeroBitplanes;
            if (planes <= 0 || planes > 30) return;
            var coder = new BlockCoder(w, h, band.Orientation, tc.Style.BlockStyle);
            coder.Decode(block, planes, body);
            // reconstruction (one fractional bit)
            int roi = tc.RoiShift;
            for (int y = 0; y < h; y++)
            {
                int target = (block.Y0 - band.Y0 + y) * band.Width + (block.X0 - band.X0);
                for (int x = 0; x < w; x++)
                {
                    int mag = coder.Magnitude(x, y);
                    if (mag == 0) continue;
                    if (roi > 0 && mag >= 1 << (roi + 1)) mag >>= roi;
                    bool negative = coder.Negative(x, y);
                    if (band.Int != null)
                    {
                        int v = mag >> 1;
                        band.Int[target + x] = negative ? -v : v;
                    }
                    else
                    {
                        float v = (float)(mag * band.Step / 2);
                        band.Float![target + x] = negative ? -v : v;
                    }
                }
            }
        }

        /// <summary>The bit-plane decoder of a code-block (ISO/IEC 15444-1, Annex D).</summary>
        private sealed class BlockCoder
        {
            private const int Stride = 2; // padding columns
            private readonly int _w, _h, _orientation, _style, _stride;
            private readonly int[] _mag;
            private readonly byte[] _negative, _significant, _visited, _refined;
            private readonly byte[] _contexts = new byte[19];
            private MqDecoder? _mq;
            private RawReader? _raw;

            public BlockCoder(int w, int h, int orientation, int style)
            {
                _w = w;
                _h = h;
                _orientation = orientation;
                _style = style;
                _stride = w + Stride;
                int size = _stride * (h + 2);
                _mag = new int[w * h];
                _negative = new byte[w * h];
                _significant = new byte[size];
                _visited = new byte[w * h];
                _refined = new byte[w * h];
                ResetContexts();
            }

            public int Magnitude(int x, int y) => _mag[y * _w + x];

            public bool Negative(int x, int y) => _negative[y * _w + x] != 0;

            private void ResetContexts()
            {
                Array.Clear(_contexts);
                _contexts[0] = 4 << 1;  // state 4
                _contexts[17] = 3 << 1; // run-length: state 3
                _contexts[18] = 46 << 1; // uniform: state 46
            }

            private int S(int x, int y) => _significant[(y + 1) * _stride + x + 1];

            private sealed class RawReader
            {
                private readonly byte[] _d;
                private int _p, _bits, _count;
                private readonly int _end;

                public RawReader(byte[] d, int start, int end)
                {
                    _d = d;
                    _p = start;
                    _end = end;
                }

                public int Read()
                {
                    if (_count == 0)
                    {
                        bool stuffed = _bits == 0xFF;
                        _bits = _p < _end ? _d[_p] : 0xFF;
                        _p++;
                        _count = stuffed ? 7 : 8;
                    }
                    _count--;
                    return (_bits >> _count) & 1;
                }
            }

            public void Decode(CodeBlock block, int planes, byte[] source)
            {
                int pass = 0, plane = planes - 1, type = 2; // the first pass is a cleanup pass
                foreach (var segment in block.Segments)
                {
                    // the segment's data
                    int length = segment.Chunks.Sum(c => c.Length);
                    var data = new byte[length + 2];
                    int p = 0;
                    foreach (var (start, len) in segment.Chunks)
                    {
                        Array.Copy(source, start, data, p, Math.Max(0, Math.Min(len, source.Length - start)));
                        p += len;
                    }
                    bool raw = (_style & Bypass) != 0 && pass >= 10 && type != 2;
                    if (raw) _raw = new RawReader(data, 0, length);
                    else _mq = new MqDecoder(data, 0, length);
                    for (int i = 0; i < segment.Passes && plane >= 0; i++)
                    {
                        if ((_style & Reset) != 0 && pass > 0) ResetContexts();
                        switch (type)
                        {
                            case 0:
                                SignificancePass(plane, raw);
                                break;
                            case 1:
                                RefinementPass(plane, raw);
                                break;
                            default:
                                CleanupPass(plane);
                                break;
                        }
                        pass++;
                        if (++type == 3)
                        {
                            type = 0;
                            plane--;
                        }
                    }
                }
            }

            private int Neighbors(int x, int y, out int h, out int v, out int d)
            {
                bool causal = (_style & VerticalCausal) != 0 && (y & 3) == 3;
                h = S(x - 1, y) + S(x + 1, y);
                v = S(x, y - 1) + (causal ? 0 : S(x, y + 1));
                d = S(x - 1, y - 1) + S(x + 1, y - 1) + (causal ? 0 : S(x - 1, y + 1) + S(x + 1, y + 1));
                return h + v + d;
            }

            private int SignificanceContext(int x, int y)
            {
                Neighbors(x, y, out int h, out int v, out int d);
                switch (_orientation)
                {
                    case 3: // HH
                    {
                        int hv = h + v;
                        if (d >= 3) return 8;
                        if (d == 2) return hv >= 1 ? 7 : 6;
                        if (d == 1) return hv >= 2 ? 5 : hv == 1 ? 4 : 3;
                        return hv >= 2 ? 2 : hv == 1 ? 1 : 0;
                    }
                    case 1: // HL: horizontal and vertical swapped
                        (h, v) = (v, h);
                        goto default;
                    default:
                        if (h == 2) return 8;
                        if (h == 1) return v >= 1 ? 7 : d >= 1 ? 6 : 5;
                        if (v == 2) return 4;
                        if (v == 1) return 3;
                        return d >= 2 ? 2 : d == 1 ? 1 : 0;
                }
            }

            private int SignContribution(int x, int y)
            {
                if ((uint)x >= (uint)_w || (uint)y >= (uint)_h || S(x, y) == 0) return 0;
                return _negative[y * _w + x] != 0 ? -1 : 1;
            }

            private int DecodeSign(int x, int y, bool raw)
            {
                if (raw) return _raw!.Read();
                bool causal = (_style & VerticalCausal) != 0 && (y & 3) == 3;
                int h = Math.Clamp(SignContribution(x - 1, y) + SignContribution(x + 1, y), -1, 1);
                int v = Math.Clamp(SignContribution(x, y - 1) + (causal ? 0 : SignContribution(x, y + 1)), -1, 1);
                int context, xor;
                if (h == 1) { context = v == 1 ? 13 : v == 0 ? 12 : 11; xor = 0; }
                else if (h == 0) { context = v == 0 ? 9 : 10; xor = v == -1 ? 1 : 0; }
                else { context = v == 1 ? 11 : v == 0 ? 12 : 13; xor = 1; }
                return _mq!.Decode(_contexts, context) ^ xor;
            }

            private void BecomeSignificant(int x, int y, int plane, bool raw)
            {
                int sign = DecodeSign(x, y, raw);
                int i = y * _w + x;
                _negative[i] = (byte)sign;
                _mag[i] = 3 << plane; // 1.5 in units of the plane (one fractional bit)
                _significant[(y + 1) * _stride + x + 1] = 1;
            }

            private void SignificancePass(int plane, bool raw)
            {
                for (int y0 = 0; y0 < _h; y0 += 4)
                    for (int x = 0; x < _w; x++)
                        for (int y = y0; y < Math.Min(y0 + 4, _h); y++)
                        {
                            int i = y * _w + x;
                            if (S(x, y) != 0 || Neighbors(x, y, out _, out _, out _) == 0) continue;
                            int bit = raw ? _raw!.Read() : _mq!.Decode(_contexts, SignificanceContext(x, y));
                            _visited[i] = 1;
                            if (bit != 0) BecomeSignificant(x, y, plane, raw);
                        }
            }

            private void RefinementPass(int plane, bool raw)
            {
                int half = 1 << plane;
                for (int y0 = 0; y0 < _h; y0 += 4)
                    for (int x = 0; x < _w; x++)
                        for (int y = y0; y < Math.Min(y0 + 4, _h); y++)
                        {
                            int i = y * _w + x;
                            if (S(x, y) == 0 || _visited[i] != 0) continue;
                            int bit;
                            if (raw) bit = _raw!.Read();
                            else
                            {
                                int context = _refined[i] != 0 ? 16 : Neighbors(x, y, out _, out _, out _) == 0 ? 14 : 15;
                                bit = _mq!.Decode(_contexts, context);
                            }
                            _mag[i] += bit != 0 ? half : -half;
                            _refined[i] = 1;
                        }
            }

            private void CleanupPass(int plane)
            {
                for (int y0 = 0; y0 < _h; y0 += 4)
                    for (int x = 0; x < _w; x++)
                    {
                        int y = y0;
                        if (y0 + 3 < _h)
                        {
                            // run-length mode: four insignificant, unvisited samples without significant neighbors
                            bool run = true;
                            for (int k = 0; k < 4 && run; k++)
                            {
                                int i = (y0 + k) * _w + x;
                                run = S(x, y0 + k) == 0 && _visited[i] == 0 && Neighbors(x, y0 + k, out _, out _, out _) == 0;
                            }
                            if (run)
                            {
                                if (_mq!.Decode(_contexts, 17) == 0) continue;
                                int r = _mq.Decode(_contexts, 18) << 1;
                                r |= _mq.Decode(_contexts, 18);
                                y = y0 + r;
                                BecomeSignificant(x, y, plane, raw: false);
                                y++;
                            }
                        }
                        for (; y < Math.Min(y0 + 4, _h); y++)
                        {
                            int i = y * _w + x;
                            if (S(x, y) != 0 || _visited[i] != 0) continue;
                            if (_mq!.Decode(_contexts, SignificanceContext(x, y)) != 0) BecomeSignificant(x, y, plane, raw: false);
                        }
                    }
                Array.Clear(_visited);
                if ((_style & SegmentationSymbols) != 0)
                    for (int k = 0; k < 4; k++) _mq!.Decode(_contexts, 18);
            }
        }

        // -------------------------------------------------------------- inverse wavelet transform

        private static int[] InverseTransformInt(TileComponent tc)
        {
            var res0 = tc.Resolutions[0];
            int w = res0.X1 - res0.X0, h = res0.Y1 - res0.Y0;
            var current = res0.Bands[0].Int!;
            for (int r = 1; r < tc.Resolutions.Length; r++)
            {
                var res = tc.Resolutions[r];
                int nw = res.X1 - res.X0, nh = res.Y1 - res.Y0;
                var a = new int[(long)nw * nh];
                Interleave(a, nw, nh, res.X0, res.Y0, current, w, res.Bands[0].Int!, res.Bands[0].Width, res.Bands[1].Int!, res.Bands[1].Width,
                    res.Bands[2].Int!, res.Bands[2].Width);
                var line = new int[Math.Max(nw, nh) + 16];
                for (int y = 0; y < nh; y++)
                {
                    Array.Copy(a, (long)y * nw, line, 8, nw);
                    Lift53(line, 8, nw, res.X0);
                    Array.Copy(line, 8, a, (long)y * nw, nw);
                }
                for (int x = 0; x < nw; x++)
                {
                    for (int y = 0; y < nh; y++) line[8 + y] = a[(long)y * nw + x];
                    Lift53(line, 8, nh, res.Y0);
                    for (int y = 0; y < nh; y++) a[(long)y * nw + x] = line[8 + y];
                }
                current = a;
                w = nw;
                h = nh;
            }
            _ = h;
            return current;
        }

        private static float[] InverseTransformFloat(TileComponent tc)
        {
            var res0 = tc.Resolutions[0];
            int w = res0.X1 - res0.X0, h = res0.Y1 - res0.Y0;
            var current = res0.Bands[0].Float!;
            for (int r = 1; r < tc.Resolutions.Length; r++)
            {
                var res = tc.Resolutions[r];
                int nw = res.X1 - res.X0, nh = res.Y1 - res.Y0;
                var a = new float[(long)nw * nh];
                Interleave(a, nw, nh, res.X0, res.Y0, current, w, res.Bands[0].Float!, res.Bands[0].Width, res.Bands[1].Float!, res.Bands[1].Width,
                    res.Bands[2].Float!, res.Bands[2].Width);
                var line = new float[Math.Max(nw, nh) + 16];
                for (int y = 0; y < nh; y++)
                {
                    Array.Copy(a, (long)y * nw, line, 8, nw);
                    Lift97(line, 8, nw, res.X0);
                    Array.Copy(line, 8, a, (long)y * nw, nw);
                }
                for (int x = 0; x < nw; x++)
                {
                    for (int y = 0; y < nh; y++) line[8 + y] = a[(long)y * nw + x];
                    Lift97(line, 8, nh, res.Y0);
                    for (int y = 0; y < nh; y++) a[(long)y * nw + x] = line[8 + y];
                }
                current = a;
                w = nw;
                h = nh;
            }
            _ = h;
            return current;
        }

        /// <summary>2D interleaving (F.3.3): even positions from the low band, odd positions from the high band.</summary>
        private static void Interleave<T>(T[] a, int w, int h, int u0, int v0, T[] ll, int llWidth, T[] hl, int hlWidth, T[] lh, int lhWidth, T[] hh, int hhWidth)
        {
            int lowX0 = (u0 + 1) / 2, highX0 = u0 / 2, lowY0 = (v0 + 1) / 2, highY0 = v0 / 2;
            for (int y = 0; y < h; y++)
            {
                int gy = v0 + y;
                bool highY = (gy & 1) != 0;
                int by = highY ? (gy - 1) / 2 - highY0 : gy / 2 - lowY0;
                for (int x = 0; x < w; x++)
                {
                    int gx = u0 + x;
                    bool highX = (gx & 1) != 0;
                    int bx = highX ? (gx - 1) / 2 - highX0 : gx / 2 - lowX0;
                    T[] source;
                    int sw;
                    if (!highX && !highY) { source = ll; sw = llWidth; }
                    else if (highX && !highY) { source = hl; sw = hlWidth; }
                    else if (!highX) { source = lh; sw = lhWidth; }
                    else { source = hh; sw = hhWidth; }
                    long index = (long)by * sw + bx;
                    if (index >= 0 && index < source.Length && bx < sw) a[(long)y * w + x] = source[index];
                }
            }
        }

        /// <summary>Periodic symmetric extension of <paramref name="n"/> samples at <paramref name="offset"/> by 4 on each side.</summary>
        private static void Extend<T>(T[] x, int offset, int n)
        {
            for (int k = 1; k <= 4; k++)
            {
                x[offset - k] = x[offset + Mirror(-k, n)];
                x[offset + n - 1 + k] = x[offset + Mirror(n - 1 + k, n)];
            }
        }

        private static int Mirror(int i, int n)
        {
            if (n == 1) return 0;
            int period = 2 * (n - 1);
            i %= period;
            if (i < 0) i += period;
            return i < n ? i : period - i;
        }

        /// <summary>Inverse 5/3 lifting (F.3.8.1) of the interleaved samples at positions i0 ... i0 + n - 1.</summary>
        private static void Lift53(int[] x, int offset, int n, int i0)
        {
            if (n == 1)
            {
                if ((i0 & 1) != 0) x[offset] /= 2;
                return;
            }
            Extend(x, offset, n);
            int even = i0 & 1; // local index of the first even (low-pass) position
            // X(2k) = Y(2k) - floor((Y(2k-1) + Y(2k+1) + 2) / 4), then X(2k+1) = Y(2k+1) + floor((X(2k) + X(2k+2)) / 2)
            for (int k = -1; k < n + 1; k++)
                if (((k - even) & 1) == 0) x[offset + k] -= (x[offset + k - 1] + x[offset + k + 1] + 2) >> 2;
            for (int k = 0; k < n; k++)
                if (((k - even) & 1) != 0) x[offset + k] += (x[offset + k - 1] + x[offset + k + 1]) >> 1;
        }

        private const float Alpha = -1.586134342059924f, Beta = -0.052980118572961f, Gamma = 0.882911075530934f, Delta = 0.443506852043971f;
        private const float K = 1.230174104914001f;

        /// <summary>Inverse 9/7 lifting (F.3.8.2) of the interleaved samples at positions i0 ... i0 + n - 1.</summary>
        private static void Lift97(float[] x, int offset, int n, int i0)
        {
            if (n == 1)
            {
                if ((i0 & 1) != 0) x[offset] /= 2;
                return;
            }
            Extend(x, offset, n);
            int even = i0 & 1;
            for (int k = -4; k < n + 4; k++) x[offset + k] *= ((k - even) & 1) == 0 ? K : 1 / K;
            for (int k = -3; k < n + 3; k++)
                if (((k - even) & 1) == 0) x[offset + k] -= Delta * (x[offset + k - 1] + x[offset + k + 1]);
            for (int k = -2; k < n + 2; k++)
                if (((k - even) & 1) != 0) x[offset + k] -= Gamma * (x[offset + k - 1] + x[offset + k + 1]);
            for (int k = -1; k < n + 1; k++)
                if (((k - even) & 1) == 0) x[offset + k] -= Beta * (x[offset + k - 1] + x[offset + k + 1]);
            for (int k = 0; k < n; k++)
                if (((k - even) & 1) != 0) x[offset + k] -= Alpha * (x[offset + k - 1] + x[offset + k + 1]);
        }
    }
}

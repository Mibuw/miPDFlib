using System.Buffers.Binary;

namespace MiPdf.Filters;

/// <summary>
/// JBIG2Decode (ISO 32000-2, 7.4.7; ITU-T T.88): bi-level images of scanned documents. Supports generic regions
/// (arithmetic templates 0-3 with typical prediction, and MMR), generic refinement regions, symbol dictionaries and
/// text regions (arithmetic and Huffman coding, refinement and aggregation), pattern dictionaries and halftone regions,
/// standard and user-defined Huffman tables, striped pages and global segments (/JBIG2Globals).
/// </summary>
internal static class Jbig2Decoder
{
    /// <summary>
    /// Decodes the embedded JBIG2 stream (and its global segments) to one bit per pixel, rows padded to whole bytes,
    /// with 0 for black as in a DeviceGray image (JBIG2 itself uses 1 for black).
    /// </summary>
    public static byte[] Decode(byte[] data, byte[]? globals, int width, int height)
    {
        var page = DecodePage(data, globals);
        int w = width > 0 ? width : page.Width, h = height > 0 ? height : page.Height;
        int rowBytes = (w + 7) / 8;
        PdfLimits.Current.CheckDecodedSize((long)rowBytes * h);
        var result = new byte[(long)rowBytes * h];
        Array.Fill(result, (byte)0xFF);
        for (int y = 0; y < Math.Min(h, page.Height); y++)
        {
            int row = y * page.Width, outRow = y * rowBytes;
            for (int x = 0; x < Math.Min(w, page.Width); x++)
                if (page.Pixels[row + x] != 0)
                    result[outRow + (x >> 3)] &= (byte)~(0x80 >> (x & 7));
        }
        return result;
    }

    /// <summary>Decodes the page of an embedded JBIG2 stream (one byte per pixel, 1 = black).</summary>
    internal static Bitmap DecodePage(byte[] data, byte[]? globals)
    {
        var context = new DecodingContext();
        t_decoded = 0;
        try
        {
            if (globals is { Length: > 0 }) context.Process(globals);
            context.Process(data);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException
                                       or InvalidOperationException or OverflowException or DivideByZeroException)
        {
            throw new PdfException("Damaged JBIG2 data.", ex);
        }
        return context.Page ?? throw new PdfException("JBIG2 data without page information.");
    }

    // ------------------------------------------------------------------ bitmaps

    // pixels of all bitmaps decoded from one stream (page bitmaps excluded): the work of a stream is limited like its size
    [ThreadStatic] private static long t_decoded;

    private static void Charge(long pixels)
    {
        t_decoded += pixels;
        if (t_decoded > PdfLimits.Current.MaxDecodedStreamSize)
            throw new PdfException($"JBIG2 data decodes to more than {PdfLimits.Current.MaxDecodedStreamSize} pixels (PdfLimits.MaxDecodedStreamSize).");
    }

    /// <summary>A bi-level bitmap, one byte per pixel (1 = black).</summary>
    internal sealed class Bitmap
    {
        public readonly int Width, Height;
        public byte[] Pixels;

        public Bitmap(int width, int height, byte fill = 0, bool page = false)
        {
            if (width < 0 || height < 0) throw new PdfException("Invalid JBIG2 bitmap size.");
            PdfLimits.Current.CheckDecodedSize((long)width * height);
            if (!page) Charge((long)width * height);
            Width = width;
            Height = height;
            Pixels = new byte[(long)width * height];
            if (fill != 0) Array.Fill(Pixels, fill);
        }

        public Bitmap(int width, int height, byte[] pixels)
        {
            Charge((long)width * height);
            Width = width;
            Height = height;
            Pixels = pixels;
        }

        public int Get(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height ? Pixels[y * Width + x] : 0;

        /// <summary>Combines <paramref name="source"/> into this bitmap at (x, y) with a combination operator.</summary>
        public void Combine(Bitmap source, int x, int y, int op)
        {
            int x0 = Math.Max(0, -x), y0 = Math.Max(0, -y);
            int x1 = Math.Min(source.Width, Width - x), y1 = Math.Min(source.Height, Height - y);
            if (x1 <= x0 || y1 <= y0) return;
            Charge((long)(x1 - x0) * (y1 - y0));
            for (int sy = y0; sy < y1; sy++)
            {
                int src = sy * source.Width, dst = (sy + y) * Width + x;
                for (int sx = x0; sx < x1; sx++)
                {
                    byte s = source.Pixels[src + sx];
                    ref byte d = ref Pixels[dst + sx];
                    d = op switch
                    {
                        0 => (byte)(d | s),
                        1 => (byte)(d & s),
                        2 => (byte)(d ^ s),
                        3 => (byte)(1 - (d ^ s)),
                        _ => s,
                    };
                }
            }
        }

        public Bitmap Crop(int x, int y, int width, int height)
        {
            var result = new Bitmap(width, height);
            for (int row = 0; row < height; row++)
                for (int col = 0; col < width; col++)
                    result.Pixels[row * width + col] = (byte)Get(x + col, y + row);
            return result;
        }
    }

    // ------------------------------------------------------------------ segments

    private sealed class Segment
    {
        public int Number, Type, Page;
        public int[] Referred = Array.Empty<int>();
        public byte[] Data = Array.Empty<byte>();
        public int Start, Length;
        public object? Result; // decoded symbol dictionary, pattern dictionary, table or intermediate region
    }

    private sealed class RegionInfo
    {
        public int Width, Height, X, Y, CombinationOperator;

        public static RegionInfo Read(byte[] d, int p) => new()
        {
            Width = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p))),
            Height = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 4))),
            X = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p + 8)),
            Y = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p + 12)),
            CombinationOperator = d[p + 16] & 7,
        };
    }

    private sealed class SymbolDictionary
    {
        public required List<Bitmap> Exported;
        public GenericContexts? Generic;
        public RefinementContexts? Refinement;
    }

    private sealed class PatternDictionary
    {
        public required List<Bitmap> Patterns;
        public int Width, Height;
    }

    private sealed class DecodingContext
    {
        private readonly Dictionary<int, Segment> _segments = new();
        public Bitmap? Page;
        private int _pageDefault;
        private bool _pageHeightUnknown;

        public void Process(byte[] data)
        {
            int p = 0;
            // a stand-alone JBIG2 file (not allowed in PDF, but found in the wild): skip its header
            if (data.Length >= 9 && data[0] == 0x97 && data[1] == 0x4A && data[2] == 0x42 && data[3] == 0x32)
            {
                int flags = data[8];
                p = 9 + ((flags & 2) == 0 ? 4 : 0);
                if ((flags & 1) == 0) throw new PdfException("JBIG2 files in random-access organization are not supported.");
            }
            while (p + 11 <= data.Length)
            {
                var segment = ReadHeader(data, ref p);
                if (segment.Length < 0 || p + segment.Length > data.Length) segment.Length = data.Length - p;
                segment.Data = data;
                segment.Start = p;
                p += segment.Length;
                _segments[segment.Number] = segment;
                if (segment.Page > 1) continue; // PDF streams contain one page
                try
                {
                    ProcessSegment(segment);
                }
                catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidOperationException)
                {
                    throw new PdfException($"Damaged JBIG2 segment {segment.Number} (type {segment.Type}).", ex);
                }
                if (segment.Type is 49 or 51) break; // end of page, end of file
            }
        }

        private static Segment ReadHeader(byte[] d, ref int p)
        {
            var s = new Segment { Number = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p)) };
            int flags = d[p + 4];
            s.Type = flags & 0x3F;
            bool largePage = (flags & 0x40) != 0;
            p += 5;
            int countByte = d[p];
            int count = countByte >> 5;
            if (count == 7)
            {
                count = (int)(BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p)) & 0x1FFFFFFF);
                p += 4 + (count + 8) / 8;
            }
            else p++;
            if (count > 1 << 16) throw new PdfException("Too many referred JBIG2 segments.");
            int size = s.Number <= 256 ? 1 : s.Number <= 65536 ? 2 : 4;
            s.Referred = new int[count];
            for (int i = 0; i < count; i++)
            {
                s.Referred[i] = size switch
                {
                    1 => d[p],
                    2 => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p)),
                    _ => (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p)),
                };
                p += size;
            }
            s.Page = largePage ? (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p)) : d[p];
            p += largePage ? 4 : 1;
            uint length = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            p += 4;
            s.Length = length == 0xFFFFFFFF ? FindUnknownLength(d, p, s.Type) : (int)Math.Min(length, int.MaxValue);
            return s;
        }

        /// <summary>An immediate generic region of unknown length ends with 0xFF 0xAC (or 0x00 0x00 for MMR) and the row count.</summary>
        private static int FindUnknownLength(byte[] d, int p, int type)
        {
            if (type is not (38 or 39) || p + 18 > d.Length) return -1;
            int height = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 4));
            bool mmr = (d[p + 17] & 1) != 0;
            for (int i = p + 18; i + 6 <= d.Length; i++)
            {
                if (d[i] != (mmr ? 0 : 0xFF) || d[i + 1] != (mmr ? 0 : 0xAC)) continue;
                if (BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i + 2)) is var rows && rows <= (uint)height) return i + 6 - p;
            }
            return d.Length - p;
        }

        private IEnumerable<Segment> Referred(Segment s) =>
            s.Referred.Select(n => _segments.TryGetValue(n, out var r) ? r : null).Where(r => r != null)!;

        private void ProcessSegment(Segment s)
        {
            var d = s.Data;
            int p = s.Start, end = s.Start + s.Length;
            // regions belong to the page: without page information they would be decoded for nothing
            if (Page == null && s.Type is 4 or 6 or 7 or 20 or 22 or 23 or 36 or 38 or 39 or 40 or 42 or 43) return;
            switch (s.Type)
            {
                case 0:
                    s.Result = DecodeSymbolDictionary(s, d, p, end);
                    break;
                case 4 or 6 or 7:
                    DecodeTextRegion(s, d, p, end);
                    break;
                case 16:
                    s.Result = DecodePatternDictionary(d, p, end);
                    break;
                case 20 or 22 or 23:
                    DecodeHalftoneRegion(s, d, p, end);
                    break;
                case 36 or 38 or 39:
                    DecodeGenericRegion(s, d, p, end);
                    break;
                case 40 or 42 or 43:
                    DecodeRefinementRegion(s, d, p, end);
                    break;
                case 48:
                    PageInformation(d, p);
                    break;
                case 50:
                    if (_pageHeightUnknown && Page != null)
                        GrowPage((int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p)) + 1);
                    break;
                case 53:
                    s.Result = HuffmanTable.FromSegment(d, p, end);
                    break;
            }
        }

        private void PageInformation(byte[] d, int p)
        {
            int width = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            uint height = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 4));
            int flags = d[p + 16];
            _pageDefault = (flags >> 2) & 1;
            _pageHeightUnknown = height == 0xFFFFFFFF;
            Page = new Bitmap(width, _pageHeightUnknown ? 0 : (int)height, (byte)_pageDefault, page: true);
        }

        private void GrowPage(int height)
        {
            if (Page == null || height <= Page.Height) return;
            var grown = new Bitmap(Page.Width, height, (byte)_pageDefault, page: true);
            Array.Copy(Page.Pixels, grown.Pixels, Page.Pixels.Length);
            Page = grown;
        }

        private void PlaceRegion(Segment s, RegionInfo info, Bitmap region)
        {
            if (s.Type is 4 or 20 or 36 or 40)
            {
                s.Result = region; // intermediate region, used by a later refinement
                return;
            }
            if (Page == null) return;
            if (_pageHeightUnknown) GrowPage(info.Y + region.Height);
            Page.Combine(region, info.X, info.Y, info.CombinationOperator);
        }

        // -------------------------------------------------------------- generic and refinement regions

        private void DecodeGenericRegion(Segment s, byte[] d, int p, int end)
        {
            var info = RegionInfo.Read(d, p);
            p += 17;
            int flags = d[p++];
            bool mmr = (flags & 1) != 0;
            int template = (flags >> 1) & 3;
            bool tpgdon = (flags & 8) != 0;
            var at = new sbyte[template == 0 ? 8 : 2];
            if (!mmr)
            {
                for (int i = 0; i < at.Length; i++) at[i] = (sbyte)d[p + i];
                p += at.Length;
            }
            Bitmap region;
            if (mmr)
            {
                region = new Bitmap(info.Width, info.Height, CcittFaxDecoder.DecodeMmr(d, p, end - p, info.Width, info.Height, out _));
            }
            else
            {
                var decoder = new MqDecoder(d, p, end);
                region = DecodeGeneric(decoder, new GenericContexts(), info.Width, info.Height, template, tpgdon, null, at);
            }
            PlaceRegion(s, info, region);
        }

        private void DecodeRefinementRegion(Segment s, byte[] d, int p, int end)
        {
            var info = RegionInfo.Read(d, p);
            p += 17;
            int flags = d[p++];
            int template = flags & 1;
            bool tpgron = (flags & 2) != 0;
            var at = new sbyte[4];
            if (template == 0)
            {
                for (int i = 0; i < 4; i++) at[i] = (sbyte)d[p + i];
                p += 4;
            }
            // the reference: an intermediate region, else the page area under the region
            var reference = Referred(s).Select(r => r.Result).OfType<Bitmap>().FirstOrDefault()
                ?? Page?.Crop(info.X, info.Y, info.Width, info.Height)
                ?? new Bitmap(info.Width, info.Height);
            var decoder = new MqDecoder(d, p, end);
            var region = DecodeRefinement(decoder, new RefinementContexts(), info.Width, info.Height, template, reference, 0, 0, tpgron, at);
            if (s.Type is not 40 && Page != null && !Referred(s).Any(r => r.Result is Bitmap))
            {
                // refinement of the page itself replaces the area
                if (_pageHeightUnknown) GrowPage(info.Y + region.Height);
                Page.Combine(region, info.X, info.Y, 4);
                return;
            }
            PlaceRegion(s, info, region);
        }

        // -------------------------------------------------------------- symbol dictionary

        private SymbolDictionary DecodeSymbolDictionary(Segment s, byte[] d, int p, int end)
        {
            int flags = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p));
            p += 2;
            bool huffman = (flags & 1) != 0, refAgg = (flags & 2) != 0;
            int huffDh = (flags >> 2) & 3, huffDw = (flags >> 4) & 3, huffBmSize = (flags >> 6) & 1, huffAggInst = (flags >> 7) & 1;
            bool contextUsed = (flags & 0x100) != 0, contextRetained = (flags & 0x200) != 0;
            int template = (flags >> 10) & 3, rTemplate = (flags >> 12) & 1;
            var at = new sbyte[huffman ? 0 : template == 0 ? 8 : 2];
            for (int i = 0; i < at.Length; i++) at[i] = (sbyte)d[p + i];
            p += at.Length;
            var rat = new sbyte[4];
            if (refAgg && rTemplate == 0)
            {
                for (int i = 0; i < 4; i++) rat[i] = (sbyte)d[p + i];
                p += 4;
            }
            int numExported = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            int numNew = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 4));
            p += 8;
            if (numNew < 0 || numNew > 1 << 20 || numExported < 0) throw new PdfException("Invalid JBIG2 symbol count.");

            var referred = Referred(s).ToList();
            var input = referred.Select(r => r.Result).OfType<SymbolDictionary>().SelectMany(x => x.Exported).ToList();
            var userTables = new Queue<HuffmanTable>(referred.Select(r => r.Result).OfType<HuffmanTable>());
            var lastDictionary = referred.Select(r => r.Result).OfType<SymbolDictionary>().LastOrDefault();

            HuffmanTable? tDh = null, tDw = null, tBmSize = null, tAggInst = null;
            if (huffman)
            {
                tDh = huffDh switch { 0 => HuffmanTable.Standard(4), 1 => HuffmanTable.Standard(5), _ => userTables.Dequeue() };
                tDw = huffDw switch { 0 => HuffmanTable.Standard(2), 1 => HuffmanTable.Standard(3), _ => userTables.Dequeue() };
                tBmSize = huffBmSize == 0 ? HuffmanTable.Standard(1) : userTables.Dequeue();
                tAggInst = huffAggInst == 0 ? HuffmanTable.Standard(1) : userTables.Dequeue();
            }

            // reused contexts are copies: several dictionaries may continue from the same retained state
            var generic = contextUsed && lastDictionary?.Generic != null ? lastDictionary.Generic.Clone() : new GenericContexts();
            var refinement = contextUsed && lastDictionary?.Refinement != null ? lastDictionary.Refinement.Clone() : new RefinementContexts();
            var ints = new IntegerContexts();
            var decoder = huffman ? null : new MqDecoder(d, p, end);
            var bits = huffman ? new BitReader(d, p, end) : null;
            int codeLength = Math.Max(huffman ? 1 : 0, CeilLog2(input.Count + numNew));

            var newSymbols = new List<Bitmap>(numNew);
            var symbols = new List<Bitmap>(input); // input and new symbols, for refinement/aggregation
            int height = 0, emptyClasses = 0;
            while (newSymbols.Count < numNew)
            {
                // damaged data can produce height classes without symbols endlessly
                if (++emptyClasses > 1024) throw new PdfException("Invalid JBIG2 symbol dictionary.");
                int dh = huffman ? tDh!.Decode(bits!) ?? 0 : ints.Dh.Decode(decoder!) ?? 0;
                height += dh;
                if (height < 0 || height > 1 << 16) throw new PdfException("Invalid JBIG2 symbol height.");
                int width = 0, totalWidth = 0, firstOfClass = newSymbols.Count;
                var widths = new List<int>();
                while (true)
                {
                    int? dw = huffman ? tDw!.Decode(bits!) : ints.Dw.Decode(decoder!);
                    if (dw == null) break; // out of band: end of the height class
                    width += dw.Value;
                    totalWidth += width;
                    if (width < 0 || width > 1 << 16 || newSymbols.Count + widths.Count >= numNew)
                        throw new PdfException("Invalid JBIG2 symbol width.");
                    emptyClasses = 0;
                    if (huffman && !refAgg)
                    {
                        widths.Add(width); // decoded later as one collective bitmap
                        continue;
                    }
                    if (!refAgg)
                    {
                        newSymbols.Add(DecodeGeneric(decoder!, generic, width, height, template, false, null, at));
                        continue;
                    }
                    // refinement/aggregate coding
                    int instances = huffman ? tAggInst!.Decode(bits!) ?? 0 : ints.Ai.Decode(decoder!) ?? 0;
                    while (symbols.Count < input.Count + newSymbols.Count) symbols.Add(newSymbols[symbols.Count - input.Count]);
                    if (instances == 1)
                    {
                        int id, rdx, rdy;
                        if (huffman)
                        {
                            id = bits!.ReadBits(codeLength);
                            rdx = HuffmanTable.Standard(15).Decode(bits) ?? 0;
                            rdy = HuffmanTable.Standard(15).Decode(bits) ?? 0;
                            int size = HuffmanTable.Standard(1).Decode(bits) ?? 0;
                            bits.AlignToByte();
                            var sub = new MqDecoder(d, bits.Position, Math.Min(end, bits.Position + size));
                            if ((uint)id >= (uint)symbols.Count) throw new PdfException("Invalid JBIG2 symbol id.");
                            newSymbols.Add(DecodeRefinement(sub, refinement, width, height, rTemplate, symbols[id], rdx, rdy, false, rat));
                            bits.Skip(size);
                        }
                        else
                        {
                            id = ints.DecodeId(decoder!, codeLength);
                            rdx = ints.Rdx.Decode(decoder!) ?? 0;
                            rdy = ints.Rdy.Decode(decoder!) ?? 0;
                            if ((uint)id >= (uint)symbols.Count) throw new PdfException("Invalid JBIG2 symbol id.");
                            newSymbols.Add(DecodeRefinement(decoder!, refinement, width, height, rTemplate, symbols[id], rdx, rdy, false, rat));
                        }
                    }
                    else
                    {
                        var text = new TextRegionParameters
                        {
                            Huffman = huffman, Refine = true, Width = width, Height = height, Instances = instances,
                            Strips = 1, Symbols = symbols, CodeLength = codeLength, Combination = 0, DefaultPixel = 0,
                            Transposed = false, RefCorner = 1, DsOffset = 0, RTemplate = rTemplate, Rat = rat,
                        };
                        if (huffman)
                        {
                            text.Fs = HuffmanTable.Standard(6);
                            text.Ds = HuffmanTable.Standard(8);
                            text.Dt = HuffmanTable.Standard(11);
                            text.Rdw = text.Rdh = text.Rdx = text.Rdy = HuffmanTable.Standard(15);
                            text.RSize = HuffmanTable.Standard(1);
                            text.SymbolCodes = null; // fixed-length ids
                        }
                        newSymbols.Add(DecodeText(text, decoder, bits, ints, refinement, d, end));
                    }
                }
                if (huffman && !refAgg)
                {
                    // collective bitmap of the height class
                    int size = tBmSize!.Decode(bits!) ?? 0;
                    bits!.AlignToByte();
                    Bitmap collective;
                    if (size == 0)
                    {
                        collective = new Bitmap(totalWidth, height);
                        int rowBytes = (totalWidth + 7) / 8;
                        for (int y = 0; y < height; y++)
                            for (int x = 0; x < totalWidth; x++)
                            {
                                int index = bits.Position + y * rowBytes + (x >> 3);
                                if (index < end && (d[index] & (0x80 >> (x & 7))) != 0) collective.Pixels[y * totalWidth + x] = 1;
                            }
                        bits.Skip(rowBytes * height);
                    }
                    else
                    {
                        collective = new Bitmap(totalWidth, height, CcittFaxDecoder.DecodeMmr(d, bits.Position, size, totalWidth, height, out _));
                        bits.Skip(size);
                    }
                    int x0 = 0;
                    foreach (int w in widths)
                    {
                        newSymbols.Add(collective.Crop(x0, 0, w, height));
                        x0 += w;
                    }
                }
                _ = firstOfClass;
            }

            // exported symbols: runs of alternately not exported and exported symbols
            var all = input.Concat(newSymbols).ToList();
            var exported = new List<Bitmap>(Math.Min(numExported, all.Count)); // the count in the header can be damaged
            bool export = false;
            int index2 = 0, runs = 0;
            while (index2 < all.Count)
            {
                int run = huffman ? HuffmanTable.Standard(1).Decode(bits!) ?? 0 : ints.Ex.Decode(decoder!) ?? 0;
                if (run < 0 || index2 + run > all.Count || ++runs > 2 * all.Count + 2) throw new PdfException("Invalid JBIG2 export run.");
                if (export) exported.AddRange(all.GetRange(index2, run));
                index2 += run;
                export = !export;
            }
            return new SymbolDictionary
            {
                Exported = exported,
                Generic = contextRetained ? generic : null,
                Refinement = contextRetained ? refinement : null,
            };
        }

        // -------------------------------------------------------------- text region

        private void DecodeTextRegion(Segment s, byte[] d, int p, int end)
        {
            var info = RegionInfo.Read(d, p);
            p += 17;
            int flags = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p));
            p += 2;
            var t = new TextRegionParameters
            {
                Huffman = (flags & 1) != 0,
                Refine = (flags & 2) != 0,
                Strips = 1 << ((flags >> 2) & 3),
                RefCorner = (flags >> 4) & 3,
                Transposed = (flags & 0x40) != 0,
                Combination = (flags >> 7) & 3,
                DefaultPixel = (flags >> 9) & 1,
                DsOffset = ((flags >> 10) & 0x1F) is var o && o > 15 ? o - 32 : o,
                RTemplate = (flags >> 15) & 1,
                Width = info.Width,
                Height = info.Height,
            };
            int huffmanFlags = 0;
            if (t.Huffman)
            {
                huffmanFlags = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p));
                p += 2;
            }
            t.Rat = new sbyte[4];
            if (t.Refine && t.RTemplate == 0)
            {
                for (int i = 0; i < 4; i++) t.Rat[i] = (sbyte)d[p + i];
                p += 4;
            }
            t.Instances = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            p += 4;
            var referred = Referred(s).ToList();
            t.Symbols = referred.Select(r => r.Result).OfType<SymbolDictionary>().SelectMany(x => x.Exported).ToList();
            var userTables = new Queue<HuffmanTable>(referred.Select(r => r.Result).OfType<HuffmanTable>());
            t.CodeLength = CeilLog2(t.Symbols.Count);
            BitReader? bits = null;
            MqDecoder? decoder = null;
            if (t.Huffman)
            {
                HuffmanTable Pick(int value, params int[] standard) =>
                    value < standard.Length ? HuffmanTable.Standard(standard[value]) : userTables.Dequeue();
                t.Fs = Pick(huffmanFlags & 3, 6, 7);
                t.Ds = Pick((huffmanFlags >> 2) & 3, 8, 9, 10);
                t.Dt = Pick((huffmanFlags >> 4) & 3, 11, 12, 13);
                t.Rdw = Pick((huffmanFlags >> 6) & 3, 14, 15);
                t.Rdh = Pick((huffmanFlags >> 8) & 3, 14, 15);
                t.Rdx = Pick((huffmanFlags >> 10) & 3, 14, 15);
                t.Rdy = Pick((huffmanFlags >> 12) & 3, 14, 15);
                t.RSize = Pick((huffmanFlags >> 14) & 1, 1);
                bits = new BitReader(d, p, end);
                t.SymbolCodes = HuffmanTable.SymbolIdTable(bits, t.Symbols.Count);
            }
            else
            {
                decoder = new MqDecoder(d, p, end);
            }
            var region = DecodeText(t, decoder, bits, new IntegerContexts(), new RefinementContexts(), d, end);
            PlaceRegion(s, info, region);
        }

        private sealed class TextRegionParameters
        {
            public bool Huffman, Refine, Transposed;
            public int Width, Height, Instances, Strips, CodeLength, Combination, DefaultPixel, RefCorner, DsOffset, RTemplate;
            public List<Bitmap> Symbols = new();
            public sbyte[] Rat = new sbyte[4];
            public HuffmanTable? Fs, Ds, Dt, Rdw, Rdh, Rdx, Rdy, RSize, SymbolCodes;
        }

        /// <summary>The text region decoding procedure (T.88, 6.4.5).</summary>
        private static Bitmap DecodeText(TextRegionParameters t, MqDecoder? decoder, BitReader? bits, IntegerContexts ints,
            RefinementContexts refinement, byte[] d, int end)
        {
            var region = new Bitmap(t.Width, t.Height, (byte)t.DefaultPixel);
            int? Read(IntegerContext arithmetic, HuffmanTable? table) => t.Huffman ? table!.Decode(bits!) : arithmetic.Decode(decoder!);

            int stripT = -(Read(ints.Dt, t.Dt) ?? 0) * t.Strips;
            int firstS = 0, instances = 0;
            while (instances < t.Instances)
            {
                stripT += (Read(ints.Dt, t.Dt) ?? 0) * t.Strips;
                bool first = true;
                int curS = 0;
                // a strip ends with an out-of-band delta S, also after the last instance
                while (true)
                {
                    if (instances > t.Instances + 1024) throw new PdfException("Too many JBIG2 symbol instances.");
                    Charge(64); // decoding an instance takes several symbols of the arithmetic or Huffman decoder
                    if (first)
                    {
                        firstS += Read(ints.Fs, t.Fs) ?? 0;
                        curS = firstS;
                        first = false;
                    }
                    else
                    {
                        int? ds = Read(ints.Ds, t.Ds);
                        if (ds == null) break; // out of band: end of the strip
                        curS += ds.Value + t.DsOffset;
                    }
                    int curT = t.Strips == 1 ? 0 : t.Huffman ? bits!.ReadBits(CeilLog2(t.Strips)) : ints.It.Decode(decoder!) ?? 0;
                    int tt = stripT + curT;
                    int id = t.Huffman
                        ? t.SymbolCodes != null ? t.SymbolCodes.Decode(bits!) ?? 0 : bits!.ReadBits(t.CodeLength)
                        : ints.DecodeId(decoder!, t.CodeLength);
                    if ((uint)id >= (uint)t.Symbols.Count) throw new PdfException("Invalid JBIG2 symbol id.");
                    int ri = !t.Refine ? 0 : t.Huffman ? bits!.ReadBits(1) : ints.Ri.Decode(decoder!) ?? 0;
                    Bitmap symbol = t.Symbols[id];
                    if (ri != 0)
                    {
                        int rdw = Read(ints.Rdw, t.Rdw) ?? 0, rdh = Read(ints.Rdh, t.Rdh) ?? 0;
                        int rdx = Read(ints.Rdx, t.Rdx) ?? 0, rdy = Read(ints.Rdy, t.Rdy) ?? 0;
                        int w = symbol.Width + rdw, h = symbol.Height + rdh;
                        int dx = FloorDiv2(rdw) + rdx, dy = FloorDiv2(rdh) + rdy;
                        if (w < 0 || h < 0) throw new PdfException("Invalid JBIG2 refinement size.");
                        if (t.Huffman)
                        {
                            int size = t.RSize!.Decode(bits!) ?? 0;
                            bits!.AlignToByte();
                            var sub = new MqDecoder(d, bits.Position, Math.Min(end, bits.Position + size));
                            symbol = DecodeRefinement(sub, refinement, w, h, t.RTemplate, symbol, dx, dy, false, t.Rat);
                            bits.Skip(size);
                        }
                        else
                        {
                            symbol = DecodeRefinement(decoder!, refinement, w, h, t.RTemplate, symbol, dx, dy, false, t.Rat);
                        }
                    }
                    int wi = symbol.Width, hi = symbol.Height;
                    bool right = t.RefCorner is 2 or 3, bottom = t.RefCorner is 0 or 2;
                    if (!t.Transposed && right) curS += wi - 1;
                    else if (t.Transposed && bottom) curS += hi - 1;
                    int x, y;
                    if (!t.Transposed)
                    {
                        x = right ? curS - wi + 1 : curS;
                        y = bottom ? tt - hi + 1 : tt;
                    }
                    else
                    {
                        x = right ? tt - wi + 1 : tt;
                        y = bottom ? curS - hi + 1 : curS;
                    }
                    region.Combine(symbol, x, y, t.Combination);
                    if (!t.Transposed && !right) curS += wi - 1;
                    else if (t.Transposed && !bottom) curS += hi - 1;
                    instances++;
                }
            }
            return region;
        }

        private static int FloorDiv2(int value) => value >= 0 ? value / 2 : -((-value + 1) / 2);

        // -------------------------------------------------------------- patterns and halftones

        private static PatternDictionary DecodePatternDictionary(byte[] d, int p, int end)
        {
            int flags = d[p];
            bool mmr = (flags & 1) != 0;
            int template = (flags >> 1) & 3;
            int width = d[p + 1], height = d[p + 2];
            int grayMax = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 3));
            p += 7;
            if (grayMax < 0 || grayMax > 1 << 16 || width == 0) throw new PdfException("Invalid JBIG2 pattern dictionary.");
            int total = (grayMax + 1) * width;
            Bitmap collective;
            if (mmr)
            {
                collective = new Bitmap(total, height, CcittFaxDecoder.DecodeMmr(d, p, end - p, total, height, out _));
            }
            else
            {
                var at = template == 0
                    ? new sbyte[] { (sbyte)-width, 0, -3, -1, 2, -2, -2, -2 }
                    : new sbyte[] { (sbyte)-width, 0 };
                collective = DecodeGeneric(new MqDecoder(d, p, end), new GenericContexts(), total, height, template, false, null, at);
            }
            var patterns = new List<Bitmap>(grayMax + 1);
            for (int i = 0; i <= grayMax; i++) patterns.Add(collective.Crop(i * width, 0, width, height));
            return new PatternDictionary { Patterns = patterns, Width = width, Height = height };
        }

        private void DecodeHalftoneRegion(Segment s, byte[] d, int p, int end)
        {
            var info = RegionInfo.Read(d, p);
            p += 17;
            int flags = d[p++];
            bool mmr = (flags & 1) != 0;
            int template = (flags >> 1) & 3;
            bool enableSkip = (flags & 8) != 0;
            int combination = (flags >> 4) & 7;
            int defaultPixel = (flags >> 7) & 1;
            int gw = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p)), gh = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 4));
            int gx = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p + 8)), gy = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p + 12));
            int rx = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p + 16)), ry = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p + 18));
            p += 20;
            var dictionary = Referred(s).Select(r => r.Result).OfType<PatternDictionary>().FirstOrDefault()
                ?? throw new PdfException("JBIG2 halftone region without pattern dictionary.");
            if (gw < 0 || gh < 0) throw new PdfException("Invalid JBIG2 halftone grid.");
            int count = dictionary.Patterns.Count, bpp = CeilLog2(count);
            // the gray-scale image is decoded bit-plane by bit-plane: all planes count against the limit
            PdfLimits.Current.CheckDecodedSize((long)gw * gh * Math.Max(1, bpp));
            var region = new Bitmap(info.Width, info.Height, (byte)defaultPixel);

            (long X, long Y) CellPosition(int m, int n) =>
                (((long)gx + (long)m * ry + (long)n * rx) >> 8, ((long)gy + (long)m * rx - (long)n * ry) >> 8);

            Bitmap? skip = null;
            if (enableSkip)
            {
                skip = new Bitmap(gw, gh);
                for (int m = 0; m < gh; m++)
                    for (int n = 0; n < gw; n++)
                    {
                        var (x, y) = CellPosition(m, n);
                        if (x + dictionary.Width <= 0 || x >= info.Width || y + dictionary.Height <= 0 || y >= info.Height)
                            skip.Pixels[m * gw + n] = 1;
                    }
            }

            // gray-scale image (Annex C.5): bit planes from the most significant one, Gray-coded
            var gray = new int[(long)gw * gh];
            var contexts = new GenericContexts();
            var decoder = mmr ? null : new MqDecoder(d, p, end);
            var at = template == 0
                ? new sbyte[] { 3, -1, -3, -1, 2, -2, -2, -2 }
                : new sbyte[] { (sbyte)(template <= 1 ? 3 : 2), -1 };
            Bitmap? previous = null;
            for (int j = bpp - 1; j >= 0; j--)
            {
                Bitmap plane;
                if (mmr)
                {
                    plane = new Bitmap(gw, gh, CcittFaxDecoder.DecodeMmr(d, p, end - p, gw, gh, out int next));
                    p = next;
                }
                else
                {
                    plane = DecodeGeneric(decoder!, contexts, gw, gh, template, false, skip, at);
                }
                if (previous != null)
                    for (int i = 0; i < plane.Pixels.Length; i++) plane.Pixels[i] ^= previous.Pixels[i];
                for (int i = 0; i < plane.Pixels.Length; i++) gray[i] |= plane.Pixels[i] << j;
                previous = plane;
            }

            for (int m = 0; m < gh; m++)
                for (int n = 0; n < gw; n++)
                {
                    if (skip != null && skip.Pixels[m * gw + n] != 0) continue;
                    var (x, y) = CellPosition(m, n);
                    if (x + dictionary.Width <= 0 || x >= info.Width || y + dictionary.Height <= 0 || y >= info.Height) continue;
                    int value = Math.Min(gray[m * gw + n], count - 1);
                    region.Combine(dictionary.Patterns[value], (int)x, (int)y, combination);
                }
            PlaceRegion(s, info, region);
        }
    }

    private static int CeilLog2(int n)
    {
        int bits = 0;
        while ((1L << bits) < n) bits++;
        return bits;
    }

    // ------------------------------------------------------------------ generic region decoding (6.2)

    internal sealed class GenericContexts
    {
        public readonly byte[] Cx = new byte[1 << 16];

        public GenericContexts Clone()
        {
            var copy = new GenericContexts();
            Cx.CopyTo(copy.Cx, 0);
            return copy;
        }
    }

    internal sealed class RefinementContexts
    {
        public readonly byte[] Cx = new byte[1 << 13];

        public RefinementContexts Clone()
        {
            var copy = new RefinementContexts();
            Cx.CopyTo(copy.Cx, 0);
            return copy;
        }
    }

    private static readonly int[] TypicalPredictionContexts = { 0x9B25, 0x0795, 0x00E5, 0x0195 };

    /// <summary>Arithmetic generic region decoding (6.2.5) with typical prediction and skipped pixels.</summary>
    internal static Bitmap DecodeGeneric(MqDecoder decoder, GenericContexts contexts, int width, int height, int template,
        bool typicalPrediction, Bitmap? skip, sbyte[] at)
    {
        var bitmap = new Bitmap(width, height);
        var cx = contexts.Cx;
        var px = bitmap.Pixels;
        int a1x = at[0], a1y = at[1];
        int a2x = at.Length > 2 ? at[2] : 0, a2y = at.Length > 2 ? at[3] : 0;
        int a3x = at.Length > 4 ? at[4] : 0, a3y = at.Length > 4 ? at[5] : 0;
        int a4x = at.Length > 6 ? at[6] : 0, a4y = at.Length > 6 ? at[7] : 0;
        int P(int x, int y) => (uint)x < (uint)width && (uint)y < (uint)height ? px[y * width + x] : 0;
        bool ltp = false;
        for (int y = 0; y < height; y++)
        {
            if (typicalPrediction)
            {
                ltp ^= decoder.Decode(cx, TypicalPredictionContexts[template]) != 0;
                if (ltp)
                {
                    if (y > 0) Array.Copy(px, (y - 1) * width, px, y * width, width);
                    continue;
                }
            }
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (skip != null && skip.Pixels[row + x] != 0) continue;
                int c;
                switch (template)
                {
                    case 0:
                        c = P(x - 1, y) | P(x - 2, y) << 1 | P(x - 3, y) << 2 | P(x - 4, y) << 3
                            | P(x + a1x, y + a1y) << 4
                            | P(x + 2, y - 1) << 5 | P(x + 1, y - 1) << 6 | P(x, y - 1) << 7 | P(x - 1, y - 1) << 8 | P(x - 2, y - 1) << 9
                            | P(x + a2x, y + a2y) << 10 | P(x + a3x, y + a3y) << 11
                            | P(x + 1, y - 2) << 12 | P(x, y - 2) << 13 | P(x - 1, y - 2) << 14
                            | P(x + a4x, y + a4y) << 15;
                        break;
                    case 1:
                        c = P(x - 1, y) | P(x - 2, y) << 1 | P(x - 3, y) << 2
                            | P(x + a1x, y + a1y) << 3
                            | P(x + 2, y - 1) << 4 | P(x + 1, y - 1) << 5 | P(x, y - 1) << 6 | P(x - 1, y - 1) << 7 | P(x - 2, y - 1) << 8
                            | P(x + 2, y - 2) << 9 | P(x + 1, y - 2) << 10 | P(x, y - 2) << 11 | P(x - 1, y - 2) << 12;
                        break;
                    case 2:
                        c = P(x - 1, y) | P(x - 2, y) << 1
                            | P(x + a1x, y + a1y) << 2
                            | P(x + 1, y - 1) << 3 | P(x, y - 1) << 4 | P(x - 1, y - 1) << 5 | P(x - 2, y - 1) << 6
                            | P(x + 1, y - 2) << 7 | P(x, y - 2) << 8 | P(x - 1, y - 2) << 9;
                        break;
                    default:
                        c = P(x - 1, y) | P(x - 2, y) << 1 | P(x - 3, y) << 2 | P(x - 4, y) << 3
                            | P(x + a1x, y + a1y) << 4
                            | P(x + 1, y - 1) << 5 | P(x, y - 1) << 6 | P(x - 1, y - 1) << 7 | P(x - 2, y - 1) << 8 | P(x - 3, y - 1) << 9;
                        break;
                }
                px[row + x] = (byte)decoder.Decode(cx, c);
            }
        }
        return bitmap;
    }

    /// <summary>Generic refinement region decoding (6.3.5); the reference pixel of (x, y) is (x - dx, y - dy).</summary>
    internal static Bitmap DecodeRefinement(MqDecoder decoder, RefinementContexts contexts, int width, int height, int template,
        Bitmap reference, int dx, int dy, bool typicalPrediction, sbyte[] at)
    {
        var bitmap = new Bitmap(width, height);
        var cx = contexts.Cx;
        var px = bitmap.Pixels;
        int P(int x, int y) => (uint)x < (uint)width && (uint)y < (uint)height ? px[y * width + x] : 0;
        int R(int x, int y) => reference.Get(x - dx, y - dy);
        bool ltp = false;
        for (int y = 0; y < height; y++)
        {
            if (typicalPrediction)
            {
                ltp ^= decoder.Decode(cx, template == 0 ? 0x0100 : 0x0080) != 0;
            }
            for (int x = 0; x < width; x++)
            {
                if (ltp)
                {
                    // typical prediction: a uniform 3x3 reference neighborhood gives the pixel
                    int v = R(x, y);
                    if (R(x - 1, y - 1) == v && R(x, y - 1) == v && R(x + 1, y - 1) == v && R(x - 1, y) == v && R(x + 1, y) == v
                        && R(x - 1, y + 1) == v && R(x, y + 1) == v && R(x + 1, y + 1) == v)
                    {
                        px[y * width + x] = (byte)v;
                        continue;
                    }
                }
                int c = template == 0
                    ? P(x - 1, y) | P(x + 1, y - 1) << 1 | P(x, y - 1) << 2 | P(x + at[0], y + at[1]) << 3
                      | R(x + 1, y + 1) << 4 | R(x, y + 1) << 5 | R(x - 1, y + 1) << 6
                      | R(x + 1, y) << 7 | R(x, y) << 8 | R(x - 1, y) << 9
                      | R(x + 1, y - 1) << 10 | R(x, y - 1) << 11 | R(x + at[2], y + at[3]) << 12
                    : P(x - 1, y) | P(x + 1, y - 1) << 1 | P(x, y - 1) << 2 | P(x - 1, y - 1) << 3
                      | R(x + 1, y + 1) << 4 | R(x, y + 1) << 5 | R(x + 1, y) << 6 | R(x, y) << 7 | R(x - 1, y) << 8 | R(x, y - 1) << 9;
                px[y * width + x] = (byte)decoder.Decode(cx, c);
            }
        }
        return bitmap;
    }

    /// <summary>An integer arithmetic decoding procedure (Annex A.2) with its 512 contexts.</summary>
    internal sealed class IntegerContext
    {
        private readonly byte[] _cx = new byte[512];

        /// <summary>The decoded integer, or null for out of band.</summary>
        public int? Decode(MqDecoder decoder)
        {
            int prev = 1;
            int ReadBits(int count)
            {
                int v = 0;
                for (int i = 0; i < count; i++)
                {
                    int bit = decoder.Decode(_cx, prev);
                    prev = prev < 256 ? (prev << 1) | bit : (((prev << 1) | bit) & 511) | 256;
                    v = (v << 1) | bit;
                }
                return v;
            }
            int sign = ReadBits(1);
            long value = ReadBits(1) == 0 ? ReadBits(2)
                : ReadBits(1) == 0 ? ReadBits(4) + 4
                : ReadBits(1) == 0 ? ReadBits(6) + 20
                : ReadBits(1) == 0 ? ReadBits(8) + 84
                : ReadBits(1) == 0 ? ReadBits(12) + 340
                : (uint)ReadBits(32) + 4436L;
            if (sign == 0) return (int)Math.Min(value, int.MaxValue);
            return value > 0 ? (int)-Math.Min(value, int.MaxValue) : null;
        }
    }

    internal sealed class IntegerContexts
    {
        public readonly IntegerContext Dh = new(), Dw = new(), Ex = new(), Ai = new(), Dt = new(), Fs = new(), Ds = new(), It = new(),
            Ri = new(), Rdw = new(), Rdh = new(), Rdx = new(), Rdy = new();

        private byte[] _id = Array.Empty<byte>();

        /// <summary>IAID: a symbol id of <paramref name="length"/> bits (A.3).</summary>
        public int DecodeId(MqDecoder decoder, int length)
        {
            if (_id.Length != 1 << length) _id = new byte[1 << Math.Min(length, 24)];
            int prev = 1;
            for (int i = 0; i < length; i++) prev = (prev << 1) | decoder.Decode(_id, prev);
            return prev - (1 << length);
        }
    }

    // ------------------------------------------------------------------ Huffman decoding (Annex B)

    internal sealed class BitReader
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _position, _bit = 8;

        public BitReader(byte[] data, int start, int end)
        {
            _data = data;
            _position = start;
            _end = Math.Min(end, data.Length);
        }

        /// <summary>The current byte position (after <see cref="AlignToByte"/>).</summary>
        public int Position => _bit == 8 ? _position : _position + 1;

        public int ReadBit()
        {
            if (_bit == 8)
            {
                if (_position >= _end) throw new PdfException("Unexpected end of JBIG2 data.");
                _bit = 0;
            }
            int value = (_data[_position] >> (7 - _bit)) & 1;
            if (++_bit == 8) _position++;
            return value;
        }

        public int ReadBits(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++) value = (value << 1) | ReadBit();
            return value;
        }

        public void AlignToByte()
        {
            if (_bit != 8)
            {
                _position++;
                _bit = 8;
            }
        }

        public void Skip(int bytes)
        {
            AlignToByte();
            _position += bytes;
        }
    }

    internal sealed class HuffmanTable
    {
        private readonly record struct Line(int RangeLow, int PrefixLength, int RangeLength, bool Lower, bool OutOfBand);

        private readonly Dictionary<(int Length, int Code), Line> _codes = new();
        private readonly int _maxLength;

        private HuffmanTable(List<Line> lines, int[]? codes = null)
        {
            // prefix codes: given (standard tables) or assigned canonically (B.3)
            codes ??= AssignCodes(lines);
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].PrefixLength > 0)
                {
                    _codes[(lines[i].PrefixLength, codes[i])] = lines[i];
                    _maxLength = Math.Max(_maxLength, lines[i].PrefixLength);
                }
        }

        private static int[] AssignCodes(List<Line> lines)
        {
            int maxLength = lines.Max(l => l.PrefixLength);
            var count = new int[maxLength + 1];
            foreach (var l in lines) count[l.PrefixLength]++;
            count[0] = 0;
            var codes = new int[lines.Count];
            int firstCode = 0;
            for (int length = 1; length <= maxLength; length++)
            {
                firstCode = (firstCode + count[length - 1]) << 1;
                int current = firstCode;
                for (int i = 0; i < lines.Count; i++)
                    if (lines[i].PrefixLength == length) codes[i] = current++;
            }
            return codes;
        }

        /// <summary>The decoded value, or null for out of band.</summary>
        public int? Decode(BitReader reader)
        {
            int code = 0;
            for (int length = 1; length <= _maxLength; length++)
            {
                code = (code << 1) | reader.ReadBit();
                if (!_codes.TryGetValue((length, code), out var line)) continue;
                if (line.OutOfBand) return null;
                long offset = line.RangeLength == 32 ? (uint)reader.ReadBits(32) : reader.ReadBits(line.RangeLength);
                long value = line.Lower ? line.RangeLow - offset : line.RangeLow + offset;
                return (int)Math.Clamp(value, int.MinValue, int.MaxValue);
            }
            throw new PdfException("Invalid JBIG2 Huffman code.");
        }

        private static readonly Dictionary<int, HuffmanTable> StandardTables = new();

        // standard tables B.1-B.15: range low, prefix length, range length, prefix code (lower range marked), then out of band
        private static readonly int[][][] StandardData =
        {
            new[] { new[] { 0, 1, 4, 0x0 }, new[] { 16, 2, 8, 0x2 }, new[] { 272, 3, 16, 0x6 }, new[] { 65808, 3, 32, 0x7 } },
            new[] { new[] { 0, 1, 0, 0x0 }, new[] { 1, 2, 0, 0x2 }, new[] { 2, 3, 0, 0x6 }, new[] { 3, 4, 3, 0xE }, new[] { 11, 5, 6, 0x1E },
                new[] { 75, 6, 32, 0x3E }, new[] { 6, 0x3F } },
            new[] { new[] { -256, 8, 8, 0xFE }, new[] { 0, 1, 0, 0x0 }, new[] { 1, 2, 0, 0x2 }, new[] { 2, 3, 0, 0x6 }, new[] { 3, 4, 3, 0xE },
                new[] { 11, 5, 6, 0x1E }, new[] { -257, 8, 32, 0xFF, 1 }, new[] { 75, 7, 32, 0x7E }, new[] { 6, 0x3E } },
            new[] { new[] { 1, 1, 0, 0x0 }, new[] { 2, 2, 0, 0x2 }, new[] { 3, 3, 0, 0x6 }, new[] { 4, 4, 3, 0xE }, new[] { 12, 5, 6, 0x1E },
                new[] { 76, 5, 32, 0x1F } },
            new[] { new[] { -255, 7, 8, 0x7E }, new[] { 1, 1, 0, 0x0 }, new[] { 2, 2, 0, 0x2 }, new[] { 3, 3, 0, 0x6 }, new[] { 4, 4, 3, 0xE },
                new[] { 12, 5, 6, 0x1E }, new[] { -256, 7, 32, 0x7F, 1 }, new[] { 76, 6, 32, 0x3E } },
            new[] { new[] { -2048, 5, 10, 0x1C }, new[] { -1024, 4, 9, 0x8 }, new[] { -512, 4, 8, 0x9 }, new[] { -256, 4, 7, 0xA },
                new[] { -128, 5, 6, 0x1D }, new[] { -64, 5, 5, 0x1E }, new[] { -32, 4, 5, 0xB }, new[] { 0, 2, 7, 0x0 },
                new[] { 128, 3, 7, 0x2 }, new[] { 256, 3, 8, 0x3 }, new[] { 512, 4, 9, 0xC }, new[] { 1024, 4, 10, 0xD },
                new[] { -2049, 6, 32, 0x3E, 1 }, new[] { 2048, 6, 32, 0x3F } },
            new[] { new[] { -1024, 4, 9, 0x8 }, new[] { -512, 3, 8, 0x0 }, new[] { -256, 4, 7, 0x9 }, new[] { -128, 5, 6, 0x1A },
                new[] { -64, 5, 5, 0x1B }, new[] { -32, 4, 5, 0xA }, new[] { 0, 4, 5, 0xB }, new[] { 32, 5, 5, 0x1C },
                new[] { 64, 5, 6, 0x1D }, new[] { 128, 4, 7, 0xC }, new[] { 256, 3, 8, 0x1 }, new[] { 512, 3, 9, 0x2 },
                new[] { 1024, 3, 10, 0x3 }, new[] { -1025, 5, 32, 0x1E, 1 }, new[] { 2048, 5, 32, 0x1F } },
            new[] { new[] { -15, 8, 3, 0xFC }, new[] { -7, 9, 1, 0x1FC }, new[] { -5, 8, 1, 0xFD }, new[] { -3, 9, 0, 0x1FD },
                new[] { -2, 7, 0, 0x7C }, new[] { -1, 4, 0, 0xA }, new[] { 0, 2, 1, 0x0 }, new[] { 2, 5, 0, 0x1A },
                new[] { 3, 6, 0, 0x3A }, new[] { 4, 3, 4, 0x4 }, new[] { 20, 6, 1, 0x3B }, new[] { 22, 4, 4, 0xB },
                new[] { 38, 4, 5, 0xC }, new[] { 70, 5, 6, 0x1B }, new[] { 134, 5, 7, 0x1C }, new[] { 262, 6, 7, 0x3C },
                new[] { 390, 7, 8, 0x7D }, new[] { 646, 6, 10, 0x3D }, new[] { -16, 9, 32, 0x1FE, 1 }, new[] { 1670, 9, 32, 0x1FF },
                new[] { 2, 0x1 } },
            new[] { new[] { -31, 8, 4, 0xFC }, new[] { -15, 9, 2, 0x1FC }, new[] { -11, 8, 2, 0xFD }, new[] { -7, 9, 1, 0x1FD },
                new[] { -5, 7, 1, 0x7C }, new[] { -3, 4, 1, 0xA }, new[] { -1, 3, 1, 0x2 }, new[] { 1, 3, 1, 0x3 },
                new[] { 3, 5, 1, 0x1A }, new[] { 5, 6, 1, 0x3A }, new[] { 7, 3, 5, 0x4 }, new[] { 39, 6, 2, 0x3B },
                new[] { 43, 4, 5, 0xB }, new[] { 75, 4, 6, 0xC }, new[] { 139, 5, 7, 0x1B }, new[] { 267, 5, 8, 0x1C },
                new[] { 523, 6, 8, 0x3C }, new[] { 779, 7, 9, 0x7D }, new[] { 1291, 6, 11, 0x3D }, new[] { -32, 9, 32, 0x1FE, 1 },
                new[] { 3339, 9, 32, 0x1FF }, new[] { 2, 0x0 } },
            new[] { new[] { -21, 7, 4, 0x7A }, new[] { -5, 8, 0, 0xFC }, new[] { -4, 7, 0, 0x7B }, new[] { -3, 5, 0, 0x18 },
                new[] { -2, 2, 2, 0x0 }, new[] { 2, 5, 0, 0x19 }, new[] { 3, 6, 0, 0x36 }, new[] { 4, 7, 0, 0x7C },
                new[] { 5, 8, 0, 0xFD }, new[] { 6, 2, 6, 0x1 }, new[] { 70, 5, 5, 0x1A }, new[] { 102, 6, 5, 0x37 },
                new[] { 134, 6, 6, 0x38 }, new[] { 198, 6, 7, 0x39 }, new[] { 326, 6, 8, 0x3A }, new[] { 582, 6, 9, 0x3B },
                new[] { 1094, 6, 10, 0x3C }, new[] { 2118, 7, 11, 0x7D }, new[] { -22, 8, 32, 0xFE, 1 }, new[] { 4166, 8, 32, 0xFF },
                new[] { 2, 0x2 } },
            new[] { new[] { 1, 1, 0, 0x0 }, new[] { 2, 2, 1, 0x2 }, new[] { 4, 4, 0, 0xC }, new[] { 5, 4, 1, 0xD }, new[] { 7, 5, 1, 0x1C },
                new[] { 9, 5, 2, 0x1D }, new[] { 13, 6, 2, 0x3C }, new[] { 17, 7, 2, 0x7A }, new[] { 21, 7, 3, 0x7B },
                new[] { 29, 7, 4, 0x7C }, new[] { 45, 7, 5, 0x7D }, new[] { 77, 7, 6, 0x7E }, new[] { 141, 7, 32, 0x7F } },
            new[] { new[] { 1, 1, 0, 0x0 }, new[] { 2, 2, 0, 0x2 }, new[] { 3, 3, 1, 0x6 }, new[] { 5, 5, 0, 0x1C }, new[] { 6, 5, 1, 0x1D },
                new[] { 8, 6, 1, 0x3C }, new[] { 10, 7, 0, 0x7A }, new[] { 11, 7, 1, 0x7B }, new[] { 13, 7, 2, 0x7C },
                new[] { 17, 7, 3, 0x7D }, new[] { 25, 7, 4, 0x7E }, new[] { 41, 8, 5, 0xFE }, new[] { 73, 8, 32, 0xFF } },
            new[] { new[] { 1, 1, 0, 0x0 }, new[] { 2, 3, 0, 0x4 }, new[] { 3, 4, 0, 0xC }, new[] { 4, 5, 0, 0x1C }, new[] { 5, 4, 1, 0xD },
                new[] { 7, 3, 3, 0x5 }, new[] { 15, 6, 1, 0x3A }, new[] { 17, 6, 2, 0x3B }, new[] { 21, 6, 3, 0x3C },
                new[] { 29, 6, 4, 0x3D }, new[] { 45, 6, 5, 0x3E }, new[] { 77, 7, 6, 0x7E }, new[] { 141, 7, 32, 0x7F } },
            new[] { new[] { -2, 3, 0, 0x4 }, new[] { -1, 3, 0, 0x5 }, new[] { 0, 1, 0, 0x0 }, new[] { 1, 3, 0, 0x6 }, new[] { 2, 3, 0, 0x7 } },
            new[] { new[] { -24, 7, 4, 0x7C }, new[] { -8, 6, 2, 0x3C }, new[] { -4, 5, 1, 0x1C }, new[] { -2, 4, 0, 0xC },
                new[] { -1, 3, 0, 0x4 }, new[] { 0, 1, 0, 0x0 }, new[] { 1, 3, 0, 0x5 }, new[] { 2, 4, 0, 0xD },
                new[] { 3, 5, 1, 0x1D }, new[] { 5, 6, 2, 0x3D }, new[] { 9, 7, 4, 0x7D }, new[] { -25, 7, 32, 0x7E, 1 },
                new[] { 25, 7, 32, 0x7F } },
        };

        public static HuffmanTable Standard(int number)
        {
            lock (StandardTables)
            {
                if (StandardTables.TryGetValue(number, out var table)) return table;
                var lines = new List<Line>();
                var codes = new List<int>();
                foreach (var e in StandardData[number - 1])
                {
                    if (e.Length == 2)
                    {
                        lines.Add(new Line(0, e[0], 0, false, true));
                        codes.Add(e[1]);
                    }
                    else
                    {
                        lines.Add(new Line(e[0], e[1], e[2], e.Length > 4, false));
                        codes.Add(e[3]);
                    }
                }
                return StandardTables[number] = new HuffmanTable(lines, codes.ToArray());
            }
        }

        /// <summary>A user-defined table (code table segment, 7.4.13).</summary>
        public static HuffmanTable FromSegment(byte[] d, int p, int end)
        {
            int flags = d[p];
            bool oob = (flags & 1) != 0;
            int prefixBits = ((flags >> 1) & 7) + 1, rangeBits = ((flags >> 4) & 7) + 1;
            int low = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p + 1)), high = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p + 5));
            var reader = new BitReader(d, p + 9, end);
            var lines = new List<Line>();
            long current = low;
            while (current < high)
            {
                int prefix = reader.ReadBits(prefixBits), range = reader.ReadBits(rangeBits);
                lines.Add(new Line((int)current, prefix, range, false, false));
                current += 1L << range;
                if (lines.Count > 1 << 16) throw new PdfException("Invalid JBIG2 code table.");
            }
            lines.Add(new Line(low - 1, reader.ReadBits(prefixBits), 32, true, false));
            lines.Add(new Line(high, reader.ReadBits(prefixBits), 32, false, false));
            if (oob) lines.Add(new Line(0, reader.ReadBits(prefixBits), 0, false, true));
            return new HuffmanTable(lines);
        }

        /// <summary>The symbol id code table of a Huffman-coded text region (7.4.3.1.7).</summary>
        public static HuffmanTable SymbolIdTable(BitReader reader, int symbols)
        {
            var runLines = new List<Line>();
            for (int i = 0; i < 35; i++) runLines.Add(new Line(i, reader.ReadBits(4), 0, false, false));
            var runCodes = new HuffmanTable(runLines);
            var lines = new List<Line>(symbols);
            while (lines.Count < symbols)
            {
                int code = runCodes.Decode(reader) ?? 0;
                if (code < 32)
                {
                    lines.Add(new Line(lines.Count, code, 0, false, false));
                    continue;
                }
                int repeat, length;
                switch (code)
                {
                    case 32:
                        if (lines.Count == 0) throw new PdfException("Invalid JBIG2 symbol id table.");
                        repeat = reader.ReadBits(2) + 3;
                        length = lines[^1].PrefixLength;
                        break;
                    case 33:
                        repeat = reader.ReadBits(3) + 3;
                        length = 0;
                        break;
                    default:
                        repeat = reader.ReadBits(7) + 11;
                        length = 0;
                        break;
                }
                for (int i = 0; i < repeat && lines.Count < symbols; i++) lines.Add(new Line(lines.Count, length, 0, false, false));
            }
            reader.AlignToByte();
            return new HuffmanTable(lines);
        }
    }
}

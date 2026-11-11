using MiPdf.Graphics;
using MiPdf.Objects;
using MiPdf.PostScript.Graphics;

namespace MiPdf.PostScript.Interpreter;

/// <summary>
/// Images (PLRM 4.10): image, imagemask and colorimage with the operand and the dictionary forms (ImageType 1, 3 and 4),
/// data from strings (reused when exhausted), procedures and files, single or multiple data sources, 1 to 16 bits per
/// component. Samples in device color spaces are written as they are (12 bits become 16), Indexed images with a device
/// base keep their lookup table, other color spaces are converted (through a lookup table for one component of up to
/// 8 bits, else per sample). JPEG data read through a DCTDecode filter is written unchanged.
/// </summary>
internal sealed partial class PsInterpreter
{
    /// <summary>What an image operator was given.</summary>
    private sealed class ImageSpec
    {
        public int Width, Height, Bpc;

        /// <summary>Samples per pixel in the data (color components; for InterleaveType 1 also the mask).</summary>
        public int Components;

        /// <summary>The color space; null for a stencil mask.</summary>
        public PsColorSpace? Space;

        public double[] Decode = Array.Empty<double>();
        public bool Interpolate;
        public Matrix ImageMatrix = Matrix.Identity;
        public PsObject[] Sources = Array.Empty<PsObject>();
        public bool Multiple;
    }

    private void RegisterImageOperators()
    {
        Op("image", ip => ip.ImageOperator());
        Op("imagemask", ip => ip.ImageMaskOperator());
        Op("colorimage", ip => ip.ColorImageOperator());
    }

    private void ImageOperator()
    {
        if (Ops.Count > 0 && Ops.Peek().Type == PsType.Dictionary)
        {
            var dict = PopDict();
            int type = ImageInt(dict, "ImageType", required: true);
            switch (type)
            {
                case 1:
                    PaintImage(ImageDict(dict, GState.ColorSpace), null);
                    return;
                case 3:
                    MaskedImage(dict);
                    return;
                case 4:
                {
                    var spec = ImageDict(dict, GState.ColorSpace);
                    var keys = DictValue(dict, "MaskColor");
                    if (!keys.IsArray) throw new PsError("typecheck");
                    var values = new int[keys.AsArray.Length];
                    for (int i = 0; i < values.Length; i++)
                        values[i] = keys.AsArray[i].Type == PsType.Integer ? (int)Math.Clamp(keys.AsArray[i].Int, -1, 65535) : throw new PsError("typecheck");
                    int n = spec.Components;
                    // as Ghostscript: 16 bits (beyond the PLRM) are not supported with color key masking
                    if (spec.Bpc > 12 || values.Any(v => v < 0 || v > (1 << spec.Bpc) - 1)) throw new PsError("rangecheck");
                    int[] ranges;
                    if (values.Length == n) ranges = values.SelectMany(v => new[] { v, v }).ToArray();
                    else if (values.Length == 2 * n) ranges = values;
                    else throw new PsError("rangecheck");
                    PaintImage(spec, ranges);
                    return;
                }
                default:
                    throw new PsError("rangecheck");
            }
        }
        var source = Pop();
        var matrix = PopMatrix();
        int bpc = ImageBits((int)PopInt());
        int height = (int)Math.Clamp(PopInt(), int.MinValue, int.MaxValue);
        int width = (int)Math.Clamp(PopInt(), int.MinValue, int.MaxValue);
        PaintImage(new ImageSpec
        {
            Width = width, Height = height, Bpc = bpc, Components = 1, Space = PsColorSpace.Gray, Decode = new double[] { 0, 1 },
            ImageMatrix = matrix, Sources = new[] { source },
        }, null);
    }

    private void ImageMaskOperator()
    {
        if (Ops.Count > 0 && Ops.Peek().Type == PsType.Dictionary)
        {
            var dict = PopDict();
            if (ImageInt(dict, "ImageType", required: true) != 1) throw new PsError("rangecheck");
            var spec = ImageDict(dict, null);
            if (spec.Bpc != 1) throw new PsError("rangecheck");
            PaintImage(spec, null);
            return;
        }
        var source = Pop();
        var matrix = PopMatrix();
        bool polarity = PopBool();
        int height = (int)Math.Clamp(PopInt(), int.MinValue, int.MaxValue);
        int width = (int)Math.Clamp(PopInt(), int.MinValue, int.MaxValue);
        PaintImage(new ImageSpec
        {
            Width = width, Height = height, Bpc = 1, Components = 1, Space = null,
            Decode = polarity ? new double[] { 1, 0 } : new double[] { 0, 1 }, ImageMatrix = matrix, Sources = new[] { source },
        }, null);
    }

    private void ColorImageOperator()
    {
        int n = (int)PopInt();
        bool multiple = PopBool();
        var space = n switch
        {
            1 => PsColorSpace.Gray,
            3 => PsColorSpace.Rgb,
            4 => PsColorSpace.Cmyk,
            _ => throw new PsError("rangecheck"),
        };
        var sources = new PsObject[multiple ? n : 1];
        for (int i = sources.Length - 1; i >= 0; i--) sources[i] = Pop();
        var matrix = PopMatrix();
        int bpc = ImageBits((int)PopInt());
        int height = (int)Math.Clamp(PopInt(), int.MinValue, int.MaxValue);
        int width = (int)Math.Clamp(PopInt(), int.MinValue, int.MaxValue);
        PaintImage(new ImageSpec
        {
            Width = width, Height = height, Bpc = bpc, Components = n, Space = space,
            Decode = Enumerable.Range(0, 2 * n).Select(i => (double)(i % 2)).ToArray(), ImageMatrix = matrix,
            Sources = sources, Multiple = multiple,
        }, null);
    }

    private static int ImageBits(int bpc) => bpc is 1 or 2 or 4 or 8 or 12 or 16 ? bpc : throw new PsError("rangecheck");

    private PsObject DictValue(PsDict dict, string key) => dict.Map.TryGetValue(Key(key), out var v) ? v : PsObject.Null;

    private int ImageInt(PsDict dict, string key, bool required, int fallback = 0)
    {
        var v = DictValue(dict, key);
        if (v.Type == PsType.Null) return required ? throw new PsError("undefined") : fallback;
        if (v.Type == PsType.Integer) return (int)Math.Clamp(v.Int, int.MinValue, int.MaxValue);
        if (v.Type == PsType.Real && v.Number == Math.Floor(v.Number)) return (int)Math.Clamp(v.Number, int.MinValue, int.MaxValue);
        throw new PsError("typecheck");
    }

    /// <summary>An ImageType 1 dictionary (also the DataDict of ImageType 3); <paramref name="space"/> null for a mask.</summary>
    private ImageSpec ImageDict(PsDict dict, PsColorSpace? space, bool needSource = true)
    {
        if (space is { Family: PsColorFamily.Pattern }) throw new PsError("undefined");
        int n = space?.Components ?? 1;
        var spec = new ImageSpec
        {
            Width = ImageInt(dict, "Width", true),
            Height = ImageInt(dict, "Height", true),
            Bpc = ImageBits(ImageInt(dict, "BitsPerComponent", true)),
            Components = n,
            Space = space,
            ImageMatrix = ToMatrix(DictValue(dict, "ImageMatrix") is { Type: not PsType.Null } m ? m : throw new PsError("undefined")),
            Interpolate = DictValue(dict, "Interpolate") is { Type: PsType.Boolean } ip && ip.Bool,
            Multiple = DictValue(dict, "MultipleDataSources") is { Type: PsType.Boolean } md && md.Bool,
        };
        var decode = DictValue(dict, "Decode");
        if (decode.IsArray)
        {
            if (decode.AsArray.Length < 2 * n) throw new PsError("rangecheck");
            spec.Decode = Enumerable.Range(0, 2 * n).Select(i => decode.AsArray[i].IsNumber ? decode.AsArray[i].Number : throw new PsError("typecheck")).ToArray();
        }
        else if (decode.Type == PsType.Null) spec.Decode = DefaultDecode(space, spec.Bpc);
        else throw new PsError("typecheck");
        if (needSource)
        {
            var source = DictValue(dict, "DataSource");
            if (source.Type == PsType.Null) throw new PsError("undefined");
            if (spec.Multiple)
            {
                if (!source.IsArray || source.IsProcedure && source.AsArray.Length != n) throw new PsError("typecheck");
                if (source.AsArray.Length < n) throw new PsError("rangecheck");
                spec.Sources = Enumerable.Range(0, n).Select(i => source.AsArray[i]).ToArray();
            }
            else spec.Sources = new[] { source };
        }
        return spec;
    }

    private static double[] DefaultDecode(PsColorSpace? space, int bpc)
    {
        if (space == null) return new double[] { 0, 1 };
        if (space.Family == PsColorFamily.Indexed) return new double[] { 0, (1 << bpc) - 1 };
        return Enumerable.Range(0, 2 * space.Components).Select(i => (double)(i % 2)).ToArray();
    }

    // ---------------------------------------------------------------- reading the data

    /// <summary>Reads image data from one source: a string (reused), a procedure (one string per call) or a file.</summary>
    private sealed class ImageSource
    {
        private readonly PsInterpreter _ip;
        private readonly PsObject _source;
        private byte[] _chunk = Array.Empty<byte>();
        private int _position;
        private readonly byte[]? _string;

        public ImageSource(PsInterpreter ip, PsObject source)
        {
            if (source.Type == PsType.String) source = ip.Readable(source);
            else if (source.Type == PsType.File)
            {
                if (!source.AsFile.CanRead) throw new PsError("invalidaccess");
            }
            else if (!source.IsProcedure) throw new PsError("typecheck");
            _ip = ip;
            _source = source;
            // a string is read again and again (nothing runs in between that could change it)
            if (source.Type == PsType.String) _string = source.AsString.ToArray();
        }

        public PsFile? File => _source.Type == PsType.File ? _source.AsFile : null;

        public bool Ended { get; private set; }

        /// <summary>The next string of a procedure or string source (empty at the end).</summary>
        public byte[] NextChunk()
        {
            if (_position < _chunk.Length)
            {
                var rest = _chunk.AsSpan(_position).ToArray();
                _position = _chunk.Length;
                return rest;
            }
            if (Ended) return Array.Empty<byte>();
            byte[] chunk;
            if (_string != null) chunk = _string;
            else
            {
                var results = _ip.CallProcedure(_source, ReadOnlySpan<PsObject>.Empty);
                if (results.Length == 0 || results[^1].Type != PsType.String) throw new PsError("typecheck");
                chunk = results[^1].AsString.ToArray();
            }
            if (chunk.Length == 0) Ended = true;
            return chunk;
        }

        /// <summary>Fills <paramref name="buffer"/> as far as the data goes; returns the number of bytes.</summary>
        public int Read(Span<byte> buffer)
        {
            if (File is { } file)
            {
                int n = file.Read(buffer);
                if (n < buffer.Length) Ended = true;
                return n;
            }
            int total = 0;
            while (total < buffer.Length)
            {
                if (_position >= _chunk.Length)
                {
                    if (Ended) break;
                    _chunk = NextChunk();
                    _position = 0;
                    if (_chunk.Length == 0) break;
                }
                int take = Math.Min(buffer.Length - total, _chunk.Length - _position);
                _chunk.AsSpan(_position, take).CopyTo(buffer[total..]);
                _position += take;
                total += take;
            }
            return total;
        }
    }

    /// <summary>A buffer that grows (up to a final size) as data arrives, so short data does not allocate the whole image.</summary>
    private sealed class GrowingBuffer
    {
        private readonly long _max;

        public GrowingBuffer(long max)
        {
            _max = max;
            Data = new byte[(int)Math.Min(max, 1 << 16)];
        }

        public byte[] Data { get; private set; }

        public int Length { get; private set; }

        public bool Full => Length >= _max;

        /// <summary>Room for up to <paramref name="want"/> more bytes (less at the final size).</summary>
        public Span<byte> Space(int want)
        {
            want = (int)Math.Min(want, _max - Length);
            if (Length + want > Data.Length)
            {
                var grown = new byte[(int)Math.Min(_max, Math.Max((long)Data.Length * 2, Length + want))];
                Data.AsSpan(0, Length).CopyTo(grown);
                Data = grown;
            }
            return Data.AsSpan(Length, want);
        }

        public void Added(int count) => Length += count;
    }

    /// <summary>Reads up to <paramref name="max"/> bytes from a source.</summary>
    private static GrowingBuffer ReadUpTo(ImageSource source, long max)
    {
        var buffer = new GrowingBuffer(max);
        while (!buffer.Full)
        {
            var space = buffer.Space(1 << 20);
            int got = source.Read(space);
            buffer.Added(got);
            if (got < space.Length || source.Ended) break;
        }
        return buffer;
    }

    /// <summary>
    /// Reads the samples of an image (pixels interleaved, rows padded to whole bytes) and returns the number of complete
    /// rows read (fewer than the height if the data ends early).
    /// </summary>
    private byte[] ReadSamples(ImageSpec spec, out int rows)
    {
        int n = spec.Components, w = spec.Width, h = spec.Height, bpc = spec.Bpc;
        long rowBytes = ((long)w * n * bpc + 7) / 8;
        CheckImageSize(rowBytes * h);
        if (!spec.Multiple || n == 1 && spec.Sources.Length == 1)
        {
            var source = new ImageSource(this, spec.Sources[0]);
            var data = ReadUpTo(source, rowBytes * h);
            source.File?.SkipEod();
            rows = (int)(data.Length / Math.Max(rowBytes, 1));
            return data.Data;
        }
        // multiple sources: one plane per component, read in turn (procedures are called in order)
        long planeRow = ((long)w * bpc + 7) / 8;
        var planes = new GrowingBuffer[n];
        var sources = new ImageSource[n];
        for (int c = 0; c < n; c++)
        {
            planes[c] = new GrowingBuffer(planeRow * h);
            sources[c] = new ImageSource(this, spec.Sources[c]);
        }
        bool ended = false;
        while (!ended)
        {
            bool all = true;
            for (int c = 0; c < n; c++)
            {
                if (planes[c].Full) continue;
                all = false;
                if (sources[c].File != null)
                {
                    var space = planes[c].Space((int)Math.Min(planeRow, int.MaxValue));
                    int got = sources[c].Read(space);
                    planes[c].Added(got);
                    if (got < space.Length) ended = true;
                }
                else
                {
                    var chunk = sources[c].NextChunk();
                    if (chunk.Length == 0) ended = true;
                    var space = planes[c].Space(chunk.Length);
                    chunk.AsSpan(0, space.Length).CopyTo(space);
                    planes[c].Added(space.Length);
                }
            }
            if (all) break;
        }
        foreach (var s in sources) s.File?.SkipEod();
        rows = (int)(planes.Min(p => p.Length) / Math.Max(planeRow, 1));
        var result = new byte[rowBytes * rows];
        if (bpc == 8 || bpc == 16)
        {
            int bytes = bpc / 8;
            for (long p = 0, count = (long)w * rows; p < count; p++)
                for (int c = 0; c < n; c++)
                    for (int b = 0; b < bytes; b++)
                        result[(p * n + c) * bytes + b] = planes[c].Data[p * bytes + b];
            return result;
        }
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < n; c++)
                    PutSample(result, y * rowBytes * 8 + ((long)x * n + c) * bpc, bpc, GetSample(planes[c].Data, y * planeRow * 8 + (long)x * bpc, bpc));
        return result;
    }

    private void CheckImageSize(long bytes)
    {
        if (bytes > Limits.MaxImageBytes) throw new PsError("limitcheck");
    }

    internal static int GetSample(byte[] data, long bit, int bpc)
    {
        long i = bit >> 3;
        switch (bpc)
        {
            case 8:
                return data[i];
            case 16:
                return data[i] << 8 | data[i + 1];
            case 12:
                return (bit & 7) == 0 ? data[i] << 4 | data[i + 1] >> 4 : (data[i] & 15) << 8 | data[i + 1];
            default:
                int shift = 8 - (int)(bit & 7) - bpc;
                return data[i] >> shift & ((1 << bpc) - 1);
        }
    }

    internal static void PutSample(byte[] data, long bit, int bpc, int value)
    {
        long i = bit >> 3;
        switch (bpc)
        {
            case 8:
                data[i] = (byte)value;
                break;
            case 16:
                data[i] = (byte)(value >> 8);
                data[i + 1] = (byte)value;
                break;
            case 12:
                if ((bit & 7) == 0)
                {
                    data[i] = (byte)(value >> 4);
                    data[i + 1] = (byte)(data[i + 1] & 15 | (value & 15) << 4);
                }
                else
                {
                    data[i] = (byte)(data[i] & 0xF0 | value >> 8 & 15);
                    data[i + 1] = (byte)value;
                }
                break;
            default:
                int shift = 8 - (int)(bit & 7) - bpc;
                int mask = ((1 << bpc) - 1) << shift;
                data[i] = (byte)(data[i] & ~mask | value << shift & mask);
                break;
        }
    }

    // ---------------------------------------------------------------- painting

    /// <summary>Reads the data of an image and paints it (if a device is set up).</summary>
    private void PaintImage(ImageSpec spec, int[]? maskColor)
    {
        if (spec.Width < 0 || spec.Height < 0) throw new PsError("rangecheck");
        if (!spec.ImageMatrix.TryInvert(out var inverse)) throw new PsError("undefinedresult");
        if (spec.Width == 0 || spec.Height == 0) return;
        var output = JpegPassThrough(spec, maskColor);
        int rows = spec.Height;
        if (output == null)
        {
            var samples = ReadSamples(spec, out rows);
            if (rows == 0) return;
            output = ToImageOutput(spec, samples, rows, maskColor);
        }
        Paint(spec, inverse, rows, output);
    }

    private void Paint(ImageSpec spec, Matrix inverse, int rows, PsImageOutput? output)
    {
        if (output == null || Device == null) return;
        PsDeviceColor color = PsDeviceColor.Black;
        if (output.IsStencil)
        {
            if (CurrentDeviceColor() is not { } c) return; // patterns (stage 5) and Separation /None
            color = c;
        }
        var unitToDevice = new Matrix(spec.Width, 0, 0, -rows, 0, rows).Multiply(inverse).Multiply(GState.Ctm);
        Device.Image(GState, unitToDevice, output, color);
    }

    /// <summary>JPEG data from an unread DCTDecode filter whose image matches the image parameters is written as it is.</summary>
    private PsImageOutput? JpegPassThrough(ImageSpec spec, int[]? maskColor)
    {
        // with color key masking the samples are needed for the mask
        if (maskColor != null || spec.Multiple || spec.Bpc != 8 || spec.Sources.Length != 1 || spec.Sources[0].Type != PsType.File) return null;
        if (spec.Space is not { Family: PsColorFamily.DeviceGray or PsColorFamily.DeviceRGB or PsColorFamily.DeviceCMYK }) return null;
        if (spec.Sources[0].AsFile is not PsDctDecode { Untouched: true } dct) return null;
        dct.Load();
        if (dct.Width != spec.Width || dct.Height != spec.Height || dct.Components != spec.Components) return null;
        dct.Consume();
        dct.SkipEod();
        int? colorTransform = dct.Parameters?.Get(PdfName.Get("ColorTransform")) is PdfNumber ct ? ct.IntValue : null;
        return new PsImageOutput
        {
            Width = spec.Width, Height = spec.Height, BitsPerComponent = 8, ColorSpace = DeviceName(spec.Space.Family),
            Decode = IsDefaultDecode(spec) ? null : spec.Decode, Interpolate = spec.Interpolate, Data = dct.Jpeg, IsJpeg = true,
            ColorTransform = colorTransform,
        };
    }

    private static PdfName DeviceName(PsColorFamily family) => family switch
    {
        PsColorFamily.DeviceGray => PdfName.DeviceGray,
        PsColorFamily.DeviceRGB => PdfName.DeviceRGB,
        _ => PdfName.DeviceCMYK,
    };

    private static bool IsDefaultDecode(ImageSpec spec)
    {
        var d = DefaultDecode(spec.Space, spec.Bpc);
        return d.Length == spec.Decode.Length && d.AsSpan().SequenceEqual(spec.Decode);
    }

    /// <summary>The samples of an image as PDF image: as they are for device spaces, converted for the others.</summary>
    private PsImageOutput? ToImageOutput(ImageSpec spec, byte[] samples, int rows, int[]? maskColor)
    {
        int w = spec.Width, n = spec.Components, bpc = spec.Bpc;
        var space = spec.Space;
        if (space == null)
        {
            return new PsImageOutput
            {
                Width = w, Height = rows, BitsPerComponent = 1, Data = Crop(samples, w, 1, 1, rows),
                Decode = spec.Decode[0] == 0 ? null : new double[] { 1, 0 }, Interpolate = spec.Interpolate,
            };
        }
        double[]? decode = IsDefaultDecode(spec) ? null : spec.Decode;
        switch (space.Family)
        {
            case PsColorFamily.DeviceGray:
            case PsColorFamily.DeviceRGB:
            case PsColorFamily.DeviceCMYK:
                return Native(spec, DeviceName(space.Family), samples, rows, decode, maskColor);
            case PsColorFamily.Indexed:
            {
                var table = IndexedTable(space);
                if (table == null) break;
                if (bpc > 8)
                {
                    // PDF allows at most 8 bits per index: the indices are computed and written with 8 bits
                    var indices = new byte[(long)w * rows];
                    long rowBits = ((long)w * bpc + 7) / 8 * 8;
                    double max = (1 << bpc) - 1;
                    for (int y = 0; y < rows; y++)
                        for (int x = 0; x < w; x++)
                        {
                            int v = GetSample(samples, y * rowBits + (long)x * bpc, bpc);
                            double index = spec.Decode[0] + v * (spec.Decode[1] - spec.Decode[0]) / max;
                            indices[(long)y * w + x] = (byte)Math.Clamp((int)Math.Round(index), 0, space.HighValue);
                        }
                    return new PsImageOutput
                    {
                        Width = w, Height = rows, BitsPerComponent = 8, ColorSpace = table, Data = indices, Interpolate = spec.Interpolate,
                        Mask = maskColor != null ? ColorKeyMask(spec, samples, rows, maskColor) : null,
                    };
                }
                return new PsImageOutput
                {
                    Width = w, Height = rows, BitsPerComponent = bpc, ColorSpace = table, Decode = decode, Interpolate = spec.Interpolate,
                    Data = Crop(samples, w, n, bpc, rows), Mask = maskColor != null ? ColorKeyMask(spec, samples, rows, maskColor) : null,
                };
            }
            case PsColorFamily.Separation when space.Lookup.Type == PsType.Name && space.Lookup.AsName.Text == "None":
                return null;
        }
        if (n == 1 && bpc <= 8)
        {
            // one component: the colors of all sample values as an Indexed lookup table
            int count = 1 << bpc;
            var colors = new PsDeviceColor?[count];
            for (int v = 0; v < count; v++)
                colors[v] = ToDevice(space, new[] { spec.Decode[0] + v * (spec.Decode[1] - spec.Decode[0]) / (count - 1) }, 0);
            var (family, bytes) = ColorBytes(colors);
            return new PsImageOutput
            {
                Width = w, Height = rows, BitsPerComponent = bpc, Interpolate = spec.Interpolate, Data = Crop(samples, w, 1, bpc, rows),
                ColorSpace = new PdfArray { PdfName.Get("Indexed"), family, new PdfNumber(count - 1), new PdfString(bytes, hex: true) },
                Mask = maskColor != null ? ColorKeyMask(spec, samples, rows, maskColor) : null,
            };
        }
        return PerSample(spec, samples, rows, maskColor);
    }

    /// <summary>
    /// Device color samples as they are. 12 bits become 8 (PDF has no 12 bits; 16 bits are handled badly by some
    /// readers, Ghostscript among them); with 16 bits a decode array is applied to the samples (Ghostscript ignores it).
    /// Color key masking of converted samples becomes an explicit mask.
    /// </summary>
    private static PsImageOutput Native(ImageSpec spec, PdfName space, byte[] samples, int rows, double[]? decode, int[]? maskColor)
    {
        int w = spec.Width, n = spec.Components, bpc = spec.Bpc;
        byte[] data;
        if (bpc == 12 || bpc == 16 && decode != null)
        {
            long rowBits = ((long)w * n * bpc + 7) / 8 * 8;
            double max = (1 << bpc) - 1;
            int outBytes = bpc == 12 ? 1 : 2;
            data = new byte[(long)w * n * outBytes * rows];
            long o = 0;
            for (int y = 0; y < rows; y++)
                for (long i = 0; i < (long)w * n; i++)
                {
                    int v = GetSample(samples, y * rowBits + i * bpc, bpc);
                    if (bpc == 12) data[o++] = (byte)((v * 255 + 2047) / 4095);
                    else
                    {
                        int c = (int)(i % n);
                        int v16 = (int)Math.Round(Math.Clamp(decode![2 * c] + v * (decode[2 * c + 1] - decode[2 * c]) / max, 0, 1) * 65535);
                        data[o++] = (byte)(v16 >> 8);
                        data[o++] = (byte)v16;
                    }
                }
            return new PsImageOutput
            {
                Width = w, Height = rows, BitsPerComponent = 8 * outBytes, ColorSpace = space, Interpolate = spec.Interpolate, Data = data,
                Decode = bpc == 12 ? decode : null, Mask = maskColor != null ? ColorKeyMask(spec, samples, rows, maskColor) : null,
            };
        }
        else data = Crop(samples, w, n, bpc, rows);
        return new PsImageOutput
        {
            Width = w, Height = rows, BitsPerComponent = bpc, ColorSpace = space, Decode = decode, Interpolate = spec.Interpolate,
            Data = data, Mask = maskColor != null ? ColorKeyMask(spec, samples, rows, maskColor) : null,
        };
    }

    /// <summary>The first <paramref name="rows"/> rows of the samples.</summary>
    private static byte[] Crop(byte[] samples, int w, int n, int bpc, int rows)
    {
        long length = ((long)w * n * bpc + 7) / 8 * rows;
        return length == samples.Length ? samples : samples.AsSpan(0, (int)length).ToArray();
    }

    /// <summary>
    /// An Indexed space as PDF Indexed space: the lookup string for a device base, else the table of converted colors
    /// (null if there are more than 256 entries).
    /// </summary>
    private PdfArray? IndexedTable(PsColorSpace space)
    {
        if (space.HighValue > 255) return null;
        var baseSpace = space.Base!;
        if (baseSpace.Family is PsColorFamily.DeviceGray or PsColorFamily.DeviceRGB or PsColorFamily.DeviceCMYK && space.Lookup.Type == PsType.String)
        {
            int length = (space.HighValue + 1) * baseSpace.Components;
            return new PdfArray
            {
                PdfName.Get("Indexed"), DeviceName(baseSpace.Family), new PdfNumber(space.HighValue),
                new PdfString(space.Lookup.AsString.Span[..length].ToArray(), hex: true),
            };
        }
        var colors = new PsDeviceColor?[space.HighValue + 1];
        for (int i = 0; i < colors.Length; i++) colors[i] = ToDevice(space, new double[] { i }, 0);
        var (family, bytes) = ColorBytes(colors);
        return new PdfArray { PdfName.Get("Indexed"), family, new PdfNumber(space.HighValue), new PdfString(bytes, hex: true) };
    }

    /// <summary>Colors as 8-bit components in the family of the first color (missing colors become white).</summary>
    private static (PdfName Family, byte[] Bytes) ColorBytes(IReadOnlyList<PsDeviceColor?> colors)
    {
        var family = colors.FirstOrDefault(c => c != null)?.Family ?? PsColorFamily.DeviceGray;
        int n = family switch { PsColorFamily.DeviceGray => 1, PsColorFamily.DeviceRGB => 3, _ => 4 };
        var bytes = new byte[colors.Count * n];
        for (int i = 0; i < colors.Count; i++) WriteColor(colors[i], family, bytes.AsSpan(i * n, n));
        return (DeviceName(family), bytes);
    }

    private static void WriteColor(PsDeviceColor? color, PsColorFamily family, Span<byte> target)
    {
        static byte B(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
        if (color is not { } c)
        {
            // white
            if (family == PsColorFamily.DeviceCMYK) target.Clear();
            else target.Fill(255);
            return;
        }
        switch (family)
        {
            case PsColorFamily.DeviceGray:
                target[0] = B(c.ToGray());
                break;
            case PsColorFamily.DeviceRGB:
                var (r, g, b) = c.ToRgb();
                (target[0], target[1], target[2]) = (B(r), B(g), B(b));
                break;
            default:
                var (cc, m, y, k) = c.ToCmyk();
                (target[0], target[1], target[2], target[3]) = (B(cc), B(m), B(y), B(k));
                break;
        }
    }

    /// <summary>Converts each sample to a device color (a cache per distinct sample value).</summary>
    private PsImageOutput PerSample(ImageSpec spec, byte[] samples, int rows, int[]? maskColor)
    {
        int w = spec.Width, n = spec.Components, bpc = spec.Bpc;
        var space = spec.Space!;
        long rowBits = ((long)w * n * bpc + 7) / 8 * 8;
        double max = (1 << bpc) - 1;
        var cache = new Dictionary<ulong, PsDeviceColor?>();
        var wideCache = new Dictionary<string, PsDeviceColor?>();
        var values = new int[n];
        var decoded = new double[n];
        PsColorFamily? family = null;
        int outN = 0;
        byte[] data = Array.Empty<byte>();
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < n; c++) values[c] = GetSample(samples, y * rowBits + ((long)x * n + c) * bpc, bpc);
                // the sample values as key: packed for up to 4 components, else as text
                ulong key = 0;
                string? wideKey = null;
                if (n <= 4)
                    for (int c = 0; c < n; c++) key = key << 16 | (uint)values[c];
                else wideKey = string.Join(",", values);
                bool found = wideKey == null ? cache.TryGetValue(key, out var color) : wideCache.TryGetValue(wideKey, out color);
                if (!found)
                {
                    for (int c = 0; c < n; c++) decoded[c] = spec.Decode[2 * c] + values[c] * (spec.Decode[2 * c + 1] - spec.Decode[2 * c]) / max;
                    color = ToDevice(space, (double[])decoded.Clone(), 0);
                    if (wideKey == null)
                    {
                        if (cache.Count < 1 << 20) cache[key] = color;
                    }
                    else if (wideCache.Count < 65536) wideCache[wideKey] = color;
                }
                if (family == null)
                {
                    family = color?.Family ?? PsColorFamily.DeviceGray;
                    outN = family switch { PsColorFamily.DeviceGray => 1, PsColorFamily.DeviceRGB => 3, _ => 4 };
                    CheckImageSize((long)w * rows * outN);
                    data = new byte[(long)w * rows * outN];
                }
                WriteColor(color, family.Value, data.AsSpan((int)(((long)y * w + x) * outN), outN));
            }
        }
        return new PsImageOutput
        {
            Width = w, Height = rows, BitsPerComponent = 8, ColorSpace = DeviceName(family ?? PsColorFamily.DeviceGray), Data = data,
            Interpolate = spec.Interpolate, Mask = maskColor != null ? ColorKeyMask(spec, samples, rows, maskColor) : null,
        };
    }

    /// <summary>A stencil mask (1 = not painted) of the samples within the color key ranges (ImageType 4).</summary>
    private static PsImageOutput ColorKeyMask(ImageSpec spec, byte[] samples, int rows, int[] ranges)
    {
        int w = spec.Width, n = spec.Components, bpc = spec.Bpc;
        long rowBits = ((long)w * n * bpc + 7) / 8 * 8;
        int maskRow = (w + 7) / 8;
        var mask = new byte[(long)maskRow * rows];
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < w; x++)
            {
                bool inside = true;
                for (int c = 0; c < n && inside; c++)
                {
                    int v = GetSample(samples, y * rowBits + ((long)x * n + c) * bpc, bpc);
                    inside = v >= ranges[2 * c] && v <= ranges[2 * c + 1];
                }
                if (inside) mask[(long)y * maskRow + x / 8] |= (byte)(0x80 >> (x & 7));
            }
        return new PsImageOutput { Width = w, Height = rows, BitsPerComponent = 1, Data = mask };
    }

    // ---------------------------------------------------------------- ImageType 3

    /// <summary>ImageType 3 (PLRM 4.10.6): an image with an explicit mask, interleaved by sample, by row or separate.</summary>
    private void MaskedImage(PsDict dict)
    {
        var dataDictObject = DictValue(dict, "DataDict");
        var maskDictObject = DictValue(dict, "MaskDict");
        if (dataDictObject.Type != PsType.Dictionary || maskDictObject.Type != PsType.Dictionary) throw new PsError("typecheck");
        int interleave = ImageInt(dict, "InterleaveType", true);
        if (interleave is < 1 or > 3) throw new PsError("rangecheck");
        var data = ImageDict(dataDictObject.AsDict, GState.ColorSpace);
        var maskSpec = ImageDict(maskDictObject.AsDict, null, needSource: interleave == 3);
        if (!data.ImageMatrix.TryInvert(out var inverse)) throw new PsError("undefinedresult");
        if (data.Width <= 0 || data.Height <= 0 || maskSpec.Width <= 0 || maskSpec.Height <= 0)
        {
            if (data.Width < 0 || data.Height < 0 || maskSpec.Width < 0 || maskSpec.Height < 0) throw new PsError("rangecheck");
            return;
        }
        int w = data.Width, h = data.Height, n = data.Components, bpc = data.Bpc;
        byte[] samples, maskBits;
        int rows, maskRows;
        int maskRowBytes = (maskSpec.Width + 7) / 8;
        bool maskInverted = maskSpec.Decode[0] != 0;
        switch (interleave)
        {
            case 1:
            {
                // mask sample first in each pixel, with the bits of the image
                if (maskSpec.Width != w || maskSpec.Height != h || maskSpec.Bpc != bpc) throw new PsError("rangecheck");
                var combined = new ImageSpec { Width = w, Height = h, Bpc = bpc, Components = n + 1, Sources = data.Sources, Multiple = data.Multiple };
                if (data.Multiple && data.Sources.Length < n + 1) throw new PsError("rangecheck");
                var all = ReadSamples(combined, out rows);
                long rowBits = ((long)w * (n + 1) * bpc + 7) / 8 * 8, outRowBits = ((long)w * n * bpc + 7) / 8 * 8;
                samples = new byte[outRowBits / 8 * rows];
                maskBits = new byte[(long)maskRowBytes * rows];
                int half = 1 << (bpc - 1);
                for (int y = 0; y < rows; y++)
                    for (int x = 0; x < w; x++)
                    {
                        long p = y * rowBits + (long)x * (n + 1) * bpc;
                        if (GetSample(all, p, bpc) >= half) maskBits[(long)y * maskRowBytes + x / 8] |= (byte)(0x80 >> (x & 7));
                        for (int c = 0; c < n; c++)
                            PutSample(samples, y * outRowBits + ((long)x * n + c) * bpc, bpc, GetSample(all, p + (c + 1L) * bpc, bpc));
                    }
                maskRows = rows;
                break;
            }
            case 2:
            {
                // rows interleaved: the taller of mask and image has a whole number of rows per row of the other
                if (maskSpec.Bpc != 1 || data.Multiple) throw new PsError("rangecheck");
                int mh = maskSpec.Height;
                if (mh % h != 0 && h % mh != 0) throw new PsError("rangecheck");
                long dataRowBytes = ((long)w * n * bpc + 7) / 8;
                CheckImageSize(dataRowBytes * h + (long)maskRowBytes * mh);
                int maskPer = mh >= h ? mh / h : 1, dataPer = h > mh ? h / mh : 1, blocks = Math.Min(mh, h);
                var source = new ImageSource(this, data.Sources[0]);
                samples = new byte[dataRowBytes * h];
                maskBits = new byte[(long)maskRowBytes * mh];
                rows = maskRows = 0;
                for (int b = 0; b < blocks; b++)
                {
                    int got = ReadFully(source, maskBits, (long)maskRows * maskRowBytes, (long)maskPer * maskRowBytes);
                    maskRows += (int)(got / maskRowBytes);
                    if (got < maskPer * maskRowBytes) break;
                    got = ReadFully(source, samples, rows * dataRowBytes, dataPer * dataRowBytes);
                    rows += (int)(got / dataRowBytes);
                    if (got < dataPer * dataRowBytes) break;
                }
                source.File?.SkipEod();
                break;
            }
            default:
            {
                if (maskSpec.Bpc != 1) throw new PsError("rangecheck");
                maskBits = ReadSamples(maskSpec, out maskRows);
                samples = ReadSamples(data, out rows);
                break;
            }
        }
        if (rows == 0) return;
        if (interleave == 1) maskInverted = maskSpec.Decode[0] != 0;
        var image = ToImageOutput(new ImageSpec
        {
            Width = w, Height = h, Bpc = bpc, Components = n, Space = data.Space, Decode = data.Decode, Interpolate = data.Interpolate,
        }, samples, rows, null);
        if (image == null) return;
        // the mask covers the rows of the image that were read
        int mRows = Math.Min(maskRows, (int)Math.Ceiling((double)rows * maskSpec.Height / h));
        if (mRows == 0) return;
        var mask = new PsImageOutput
        {
            Width = maskSpec.Width, Height = mRows, BitsPerComponent = 1, Data = maskBits.AsSpan(0, mRows * maskRowBytes).ToArray(),
            Decode = maskInverted ? new double[] { 1, 0 } : null,
        };
        image = new PsImageOutput
        {
            Width = image.Width, Height = image.Height, BitsPerComponent = image.BitsPerComponent, ColorSpace = image.ColorSpace,
            Decode = image.Decode, Interpolate = image.Interpolate, Data = image.Data, Mask = mask,
        };
        Paint(data, inverse, rows, image);
    }

    private static int ReadFully(ImageSource source, byte[] data, long offset, long count)
    {
        int total = 0;
        while (total < count)
        {
            int got = source.Read(data.AsSpan((int)(offset + total), (int)Math.Min(1 << 20, count - total)));
            total += got;
            if (got == 0 || source.Ended) break;
        }
        return total;
    }
}

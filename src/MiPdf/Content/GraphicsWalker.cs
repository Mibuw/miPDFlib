using MiPdf.Graphics;
using MiPdf.Objects;

namespace MiPdf.Content;

/// <summary>
/// Walks a content stream with the current transformation matrix and reports drawn images and painted paths
/// (including the content of form XObjects). Used by image extraction and free-space detection.
/// </summary>
internal sealed class GraphicsWalker
{
    /// <summary>Called for image XObjects and inline images with the CTM (the image is the unit square in that space).</summary>
    public Action<PdfStream, Matrix, string?>? Image { get; init; }

    /// <summary>Called for painted paths with their bounding box in default user space.</summary>
    public Action<PdfRectangle>? Path { get; init; }

    private readonly HashSet<PdfStream> _active = new(ReferenceEqualityComparer.Instance);

    public void Walk(byte[] content, PdfDictionary? resources, Matrix ctm, int depth = 0)
    {
        if (depth > 16) return;
        var stack = new Stack<Matrix>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Point(double x, double y)
        {
            var (px, py) = ctm.Transform(x, y);
            minX = Math.Min(minX, px);
            minY = Math.Min(minY, py);
            maxX = Math.Max(maxX, px);
            maxY = Math.Max(maxY, py);
        }
        void ResetPath() => (minX, minY, maxX, maxY) = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);

        foreach (var op in ContentParser.Parse(content))
        {
            var o = op.Operands;
            switch (op.Operator)
            {
                case "q":
                    stack.Push(ctm);
                    break;
                case "Q":
                    if (stack.Count > 0) ctm = stack.Pop();
                    break;
                case "cm":
                    if (o.Count >= 6) ctm = new Matrix(N(o, 0), N(o, 1), N(o, 2), N(o, 3), N(o, 4), N(o, 5)).Multiply(ctm);
                    break;
                case "m":
                case "l":
                    if (o.Count >= 2) Point(N(o, 0), N(o, 1));
                    break;
                case "c":
                    for (int i = 0; i + 1 < Math.Min(o.Count, 6); i += 2) Point(N(o, i), N(o, i + 1));
                    break;
                case "v":
                case "y":
                    for (int i = 0; i + 1 < Math.Min(o.Count, 4); i += 2) Point(N(o, i), N(o, i + 1));
                    break;
                case "re":
                    if (o.Count >= 4)
                    {
                        double x = N(o, 0), y = N(o, 1), w = N(o, 2), h = N(o, 3);
                        Point(x, y);
                        Point(x + w, y + h);
                        Point(x + w, y);
                        Point(x, y + h);
                    }
                    break;
                case "S":
                case "s":
                case "f":
                case "F":
                case "f*":
                case "B":
                case "B*":
                case "b":
                case "b*":
                    if (maxX >= minX) Path?.Invoke(new PdfRectangle(minX, minY, maxX, maxY));
                    ResetPath();
                    break;
                case "n":
                    ResetPath();
                    break;
                case "BI":
                    if (op.InlineImage is { } inline) Image?.Invoke(ExpandInline(inline), ctm, null);
                    break;
                case "Do":
                    if (o.Count < 1 || o[0] is not PdfName name || resources?.GetDictionary(PdfName.XObject)?.GetStream(name) is not { } xobject) break;
                    var subtype = xobject.GetName(PdfName.Subtype);
                    if (subtype == PdfName.Image) Image?.Invoke(xobject, ctm, name.Value);
                    else if (subtype == PdfName.Form && _active.Add(xobject))
                    {
                        var matrix = xobject.GetArray(PdfName.Get("Matrix")) is { Count: 6 } m
                            ? new Matrix(m.GetDouble(0), m.GetDouble(1), m.GetDouble(2), m.GetDouble(3), m.GetDouble(4), m.GetDouble(5))
                            : Matrix.Identity;
                        try
                        {
                            Walk(xobject.GetBytes(), xobject.GetDictionary(PdfName.Resources) ?? resources, matrix.Multiply(ctm), depth + 1);
                        }
                        finally
                        {
                            _active.Remove(xobject);
                        }
                    }
                    break;
            }
        }
    }

    private static double N(IReadOnlyList<PdfObject> operands, int index) =>
        index < operands.Count && operands[index] is PdfNumber n ? n.Value : 0;

    /// <summary>Converts the abbreviated keys and names of an inline image into their full form.</summary>
    internal static PdfStream ExpandInline(PdfStream inline)
    {
        static PdfName Key(PdfName k) => k.Value switch
        {
            "W" => PdfName.Get("Width"),
            "H" => PdfName.Get("Height"),
            "BPC" => PdfName.BitsPerComponent,
            "CS" => PdfName.ColorSpace,
            "F" => PdfName.Filter,
            "DP" => PdfName.DecodeParms,
            "IM" => PdfName.Get("ImageMask"),
            "D" => PdfName.Decode,
            "I" => PdfName.Get("Interpolate"),
            _ => k,
        };
        static PdfObject Value(PdfObject v) => v switch
        {
            PdfName { Value: "G" } => PdfName.DeviceGray,
            PdfName { Value: "RGB" } => PdfName.DeviceRGB,
            PdfName { Value: "CMYK" } => PdfName.DeviceCMYK,
            PdfName { Value: "I" } => PdfName.Get("Indexed"),
            PdfName { Value: "AHx" } => PdfName.ASCIIHexDecode,
            PdfName { Value: "A85" } => PdfName.ASCII85Decode,
            PdfName { Value: "LZW" } => PdfName.LZWDecode,
            PdfName { Value: "Fl" } => PdfName.FlateDecode,
            PdfName { Value: "RL" } => PdfName.Get("RunLengthDecode"),
            PdfName { Value: "CCF" } => PdfName.CCITTFaxDecode,
            PdfName { Value: "DCT" } => PdfName.DCTDecode,
            PdfArray a => new PdfArray(a.Select(Value)),
            _ => v,
        };
        var copy = new PdfStream(Array.Empty<byte>(), compress: false);
        copy.SetRawBytes(inline.RawBytes);
        foreach (var (key, value) in inline) if (key != PdfName.Length) copy[Key(key)] = Value(value);
        copy[PdfName.Subtype] = PdfName.Image;
        return copy;
    }
}

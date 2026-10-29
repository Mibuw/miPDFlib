using System.Buffers.Binary;
using System.Text;

namespace MiPdf.PdfA;

/// <summary>
/// Generates an ICC v2 display profile for sRGB (IEC 61966-2.1): D50-adapted primaries and the exact
/// sRGB tone curve sampled with 1024 points. Used as PDF/A output intent.
/// </summary>
internal static class SrgbProfile
{
    private static readonly Lazy<byte[]> Profile = new(Build);

    public static byte[] Bytes => Profile.Value;

    private static byte[] Build()
    {
        var tags = new List<(string Signature, byte[] Data)>
        {
            ("desc", Description("sRGB IEC61966-2.1")),
            ("cprt", Text("No copyright, use freely")),
            ("wtpt", Xyz(0.9642, 1.0, 0.8249)),
            ("rXYZ", Xyz(0.4361, 0.2225, 0.0139)),
            ("gXYZ", Xyz(0.3851, 0.7169, 0.0971)),
            ("bXYZ", Xyz(0.1431, 0.0606, 0.7141)),
            ("rTRC", Curve()),
        };
        // gTRC and bTRC share the data of rTRC
        int tagCount = tags.Count + 2;
        int offset = 128 + 4 + 12 * tagCount;
        using var data = new MemoryStream();
        var table = new List<(string, int, int)>();
        foreach (var (sig, bytes) in tags)
        {
            table.Add((sig, offset + (int)data.Length, bytes.Length));
            data.Write(bytes);
            while (data.Length % 4 != 0) data.WriteByte(0);
        }
        var trc = table.Single(t => t.Item1 == "rTRC");
        table.Add(("gTRC", trc.Item2, trc.Item3));
        table.Add(("bTRC", trc.Item2, trc.Item3));

        int size = offset + (int)data.Length;
        var profile = new byte[size];
        var span = profile.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, (uint)size);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], 0x02100000);  // version 2.1
        Ascii("mntr").CopyTo(span[12..]);                               // display device
        Ascii("RGB ").CopyTo(span[16..]);
        Ascii("XYZ ").CopyTo(span[20..]);
        BinaryPrimitives.WriteUInt16BigEndian(span[24..], 2026);         // creation date
        BinaryPrimitives.WriteUInt16BigEndian(span[26..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(span[28..], 1);
        Ascii("acsp").CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32BigEndian(span[68..], S15(0.9642));   // PCS illuminant D50
        BinaryPrimitives.WriteInt32BigEndian(span[72..], S15(1.0));
        BinaryPrimitives.WriteInt32BigEndian(span[76..], S15(0.8249));
        BinaryPrimitives.WriteUInt32BigEndian(span[128..], (uint)tagCount);
        for (int i = 0; i < table.Count; i++)
        {
            var (sig, o, len) = table[i];
            Ascii(sig).CopyTo(span[(132 + 12 * i)..]);
            BinaryPrimitives.WriteUInt32BigEndian(span[(136 + 12 * i)..], (uint)o);
            BinaryPrimitives.WriteUInt32BigEndian(span[(140 + 12 * i)..], (uint)len);
        }
        data.ToArray().CopyTo(profile, offset);
        return profile;
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static int S15(double v) => (int)Math.Round(v * 65536);

    private static byte[] Xyz(double x, double y, double z)
    {
        var b = new byte[20];
        Ascii("XYZ ").CopyTo(b, 0);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(8), S15(x));
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(12), S15(y));
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16), S15(z));
        return b;
    }

    private static byte[] Text(string text)
    {
        var b = new byte[8 + text.Length + 1];
        Ascii("text").CopyTo(b, 0);
        Ascii(text).CopyTo(b, 8);
        return b;
    }

    private static byte[] Description(string text)
    {
        // textDescriptionType: ASCII part, empty Unicode part, empty ScriptCode part (67 bytes)
        var b = new byte[12 + text.Length + 1 + 4 + 4 + 2 + 1 + 67];
        Ascii("desc").CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), (uint)(text.Length + 1));
        Ascii(text).CopyTo(b, 12);
        return b;
    }

    private static byte[] Curve()
    {
        const int n = 1024;
        var b = new byte[12 + 2 * n];
        Ascii("curv").CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), n);
        for (int i = 0; i < n; i++)
        {
            double v = (double)i / (n - 1);
            double linear = v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(12 + 2 * i), (ushort)Math.Round(linear * 65535));
        }
        return b;
    }
}

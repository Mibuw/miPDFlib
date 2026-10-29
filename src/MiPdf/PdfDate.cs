using System.Globalization;

namespace MiPdf;

/// <summary>Conversion between <see cref="DateTimeOffset"/> and PDF date strings (<c>D:YYYYMMDDHHmmSSOHH'mm'</c>).</summary>
public static class PdfDate
{
    public static string Format(DateTimeOffset date)
    {
        var offset = date.Offset;
        string tz = offset == TimeSpan.Zero
            ? "Z"
            : $"{(offset < TimeSpan.Zero ? '-' : '+')}{Math.Abs(offset.Hours):D2}'{Math.Abs(offset.Minutes):D2}'";
        return "D:" + date.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + tz;
    }

    /// <summary>Parses a PDF date string. Tolerates missing parts and common deviations.</summary>
    public static DateTimeOffset? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal)) s = s[2..];
        int pos = 0;

        int Read(int digits, int fallback)
        {
            if (pos + digits > s.Length) return fallback;
            for (int i = 0; i < digits; i++)
                if (!char.IsAsciiDigit(s[pos + i])) return fallback;
            int v = int.Parse(s.AsSpan(pos, digits), CultureInfo.InvariantCulture);
            pos += digits;
            return v;
        }

        int year = Read(4, -1);
        if (year < 0) return null;
        int month = Read(2, 1), day = Read(2, 1), hour = Read(2, 0), minute = Read(2, 0), second = Read(2, 0);
        var offset = TimeSpan.Zero;
        if (pos < s.Length && (s[pos] == '+' || s[pos] == '-'))
        {
            int sign = s[pos] == '-' ? -1 : 1;
            pos++;
            int oh = Read(2, 0);
            if (pos < s.Length && s[pos] == '\'') pos++;
            int om = Read(2, 0);
            offset = new TimeSpan(sign * oh, sign * om, 0);
        }
        try
        {
            return new DateTimeOffset(year, Math.Clamp(month, 1, 12), Math.Clamp(day, 1, DateTime.DaysInMonth(year, Math.Clamp(month, 1, 12))),
                Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59), Math.Clamp(second, 0, 59), offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

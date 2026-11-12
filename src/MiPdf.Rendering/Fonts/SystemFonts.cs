using System.Collections.Concurrent;
using MiPdf.Fonts.Programs;
using MiPdf.Objects;

namespace MiPdf.Rendering.Fonts;

/// <summary>System fonts as substitutes for fonts that are not embedded (standard 14 fonts and others).</summary>
internal static class SystemFonts
{
    private static readonly ConcurrentDictionary<string, SfntFont?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Directories = BuildDirectories();

    private static string[] BuildDirectories()
    {
        var list = new List<string>();
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (!string.IsNullOrEmpty(windows)) list.Add(windows);
        list.AddRange(new[] { "/usr/share/fonts", "/usr/local/share/fonts", "/System/Library/Fonts", "/Library/Fonts" });
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) list.Add(Path.Combine(home, ".fonts"));
        return list.Where(Directory.Exists).ToArray();
    }

    // candidates per style: Windows, Liberation (metric compatible), DejaVu
    private static readonly Dictionary<string, string[]> Candidates = new(StringComparer.Ordinal)
    {
        ["sans"] = new[] { "arial.ttf", "LiberationSans-Regular.ttf", "DejaVuSans.ttf" },
        ["sans-bold"] = new[] { "arialbd.ttf", "LiberationSans-Bold.ttf", "DejaVuSans-Bold.ttf" },
        ["sans-italic"] = new[] { "ariali.ttf", "LiberationSans-Italic.ttf", "DejaVuSans-Oblique.ttf" },
        ["sans-bolditalic"] = new[] { "arialbi.ttf", "LiberationSans-BoldItalic.ttf", "DejaVuSans-BoldOblique.ttf" },
        ["serif"] = new[] { "times.ttf", "LiberationSerif-Regular.ttf", "DejaVuSerif.ttf" },
        ["serif-bold"] = new[] { "timesbd.ttf", "LiberationSerif-Bold.ttf", "DejaVuSerif-Bold.ttf" },
        ["serif-italic"] = new[] { "timesi.ttf", "LiberationSerif-Italic.ttf", "DejaVuSerif-Italic.ttf" },
        ["serif-bolditalic"] = new[] { "timesbi.ttf", "LiberationSerif-BoldItalic.ttf", "DejaVuSerif-BoldItalic.ttf" },
        ["mono"] = new[] { "cour.ttf", "LiberationMono-Regular.ttf", "DejaVuSansMono.ttf" },
        ["mono-bold"] = new[] { "courbd.ttf", "LiberationMono-Bold.ttf", "DejaVuSansMono-Bold.ttf" },
        ["mono-italic"] = new[] { "couri.ttf", "LiberationMono-Italic.ttf", "DejaVuSansMono-Oblique.ttf" },
        ["mono-bolditalic"] = new[] { "courbi.ttf", "LiberationMono-BoldItalic.ttf", "DejaVuSansMono-BoldOblique.ttf" },
        ["symbol"] = new[] { "symbol.ttf" },
        ["dingbats"] = new[] { "seguisym.ttf", "DejaVuSans.ttf" },
    };

    /// <summary>A substitute for a font by its name and font descriptor (sans, serif or monospaced; bold, italic).</summary>
    public static SfntFont? Find(string baseFont, PdfDictionary? descriptor)
    {
        string name = baseFont.Contains('+') ? baseFont[(baseFont.IndexOf('+') + 1)..] : baseFont;
        int flags = descriptor?.GetInt(PdfName.Get("Flags")) ?? 0;
        string lower = name.ToLowerInvariant();
        string family =
            lower.StartsWith("symbol", StringComparison.Ordinal) ? "symbol"
            : lower.Contains("dingbat") ? "dingbats"
            : lower.Contains("courier") || lower.Contains("mono") || (flags & 1) != 0 ? "mono"
            : lower.Contains("times") || lower.Contains("serif") && !lower.Contains("sans") || lower.Contains("georgia") || lower.Contains("garamond") || (flags & 2) != 0 && !lower.Contains("arial") && !lower.Contains("helvetica") ? "serif"
            : "sans";
        bool bold = lower.Contains("bold") || lower.Contains("black") || lower.Contains("heavy") || (descriptor?.GetDouble(PdfName.Get("FontWeight")) ?? 400) >= 600 || (flags & (1 << 18)) != 0;
        bool italic = lower.Contains("italic") || lower.Contains("oblique") || (flags & 64) != 0 || (descriptor?.GetDouble(PdfName.Get("ItalicAngle")) ?? 0) != 0;
        string key = family is "symbol" or "dingbats" ? family : family + (bold && italic ? "-bolditalic" : bold ? "-bold" : italic ? "-italic" : "");
        return Cache.GetOrAdd(key, Load) ?? (key != "sans" ? Cache.GetOrAdd("sans", Load) : null);
    }

    private static SfntFont? Load(string key)
    {
        foreach (var file in Candidates[key])
        {
            foreach (var directory in Directories)
            {
                string? path = File.Exists(Path.Combine(directory, file)) ? Path.Combine(directory, file)
                    : SafeSearch(directory, file);
                if (path == null) continue;
                try
                {
                    return new SfntFont(File.ReadAllBytes(path)) { FileName = Path.GetFileName(path) };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PdfException or IndexOutOfRangeException)
                {
                    // try the next candidate
                }
            }
        }
        return null;
    }

    private static string? SafeSearch(string directory, string file)
    {
        try
        {
            return Directory.EnumerateFiles(directory, file, new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true, MaxRecursionDepth = 4 }).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

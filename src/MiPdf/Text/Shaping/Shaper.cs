using MiPdf.Fonts.TrueType;

namespace MiPdf.Text.Shaping;

/// <summary>A positioned glyph of shaped text (visual order; positions in font units from the start of the line).</summary>
internal readonly record struct PositionedGlyph(int Gid, double X, double Y, int Cluster, string Text);

/// <summary>A run of glyphs with one direction (visual order), with its logical text.</summary>
internal sealed class ShapedRun
{
    public required List<PositionedGlyph> Glyphs { get; init; }
    public required bool RightToLeft { get; init; }
    public required string Text { get; init; }
    public double Start { get; set; }
    public double Width { get; init; }

    /// <summary>The bidi embedding level (odd = right to left).</summary>
    public int Level { get; set; }

    /// <summary>The run's range in the line's text (UTF-16 positions).</summary>
    public int LogicalStart { get; init; }
    public int LogicalEnd { get; init; }
}

/// <summary>A shaped line: runs in visual order and the total advance (font units).</summary>
internal sealed class ShapedLine
{
    public required List<ShapedRun> Runs { get; init; }
    public required string Text { get; init; }
    public double Width => Runs.Sum(r => r.Width);
}

/// <summary>
/// Text shaping for TrueType/OpenType fonts: the line is split into runs by bidi level and script, each run is
/// mapped to glyphs and transformed with the font's GSUB features (Arabic joining forms, required and optional
/// ligatures, contextual alternates, composition) and positioned with GPOS (kerning, mark attachment, cursive
/// attachment); the runs are then ordered visually (UAX #9).
/// </summary>
internal static class Shaper
{
    // feature mask bits: bit 0 = global features; the Arabic positional forms and the Indic forms that apply to parts
    // of a syllable have their own bits
    internal const uint Global = 1, Isol = 2, Fina = 4, Medi = 8, Init = 16, Rphf = 32, Pref = 64, Blwf = 128, Abvf = 256,
        Half = 512, Pstf = 1024;

    /// <summary>True if the text needs shaping (complex scripts, right-to-left text or combining marks).</summary>
    public static bool NeedsShaping(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            int cp = rune.Value;
            if (cp < 0x0300) continue;
            if (cp is >= 0x0300 and <= 0x036F) return true; // combining diacritics
            if (cp is >= 0x0900 and <= 0x0DFF) return true; // Indic scripts
            if (UnicodeTables.BidiClassOf(cp) is BidiClass.R or BidiClass.AL or BidiClass.AN) return true;
            if (UnicodeTables.IsMark(cp)) return true;
        }
        return false;
    }

    /// <summary>Shapes one line of text.</summary>
    /// <param name="font">The font program.</param>
    /// <param name="text">The text in logical order.</param>
    /// <param name="ligatures">Applies standard ligatures (liga, clig) also to text that does not need them.</param>
    /// <param name="kerning">Applies GPOS kerning (always applied to right-to-left and complex scripts).</param>
    /// <param name="paragraphLevel">0 left to right, 1 right to left, null to detect from the text.</param>
    public static ShapedLine ShapeLine(TrueTypeFont font, string text, bool ligatures, bool kerning, int? paragraphLevel = null)
    {
        // code points with their UTF-16 positions
        var cps = new List<int>(text.Length);
        var positions = new List<int>(text.Length);
        for (int i = 0; i < text.Length;)
        {
            var rune = System.Text.Rune.GetRuneAt(text, i);
            cps.Add(rune.Value);
            positions.Add(i);
            i += rune.Utf16SequenceLength;
        }
        var cpArray = cps.ToArray();
        var levels = Bidi.Levels(cpArray, paragraphLevel);

        // runs of the same level and script (common/inherited characters join the surrounding script)
        var runs = new List<(int Start, int End, int Level, UnicodeScript Script)>();
        var scripts = ResolveScripts(cpArray);
        int start = 0;
        for (int i = 1; i <= cpArray.Length; i++)
        {
            if (i < cpArray.Length && levels[i] == levels[start] && scripts[i] == scripts[start]) continue;
            runs.Add((start, i, levels[start], scripts[start]));
            start = i;
        }

        // shape each run in logical order, then order the runs visually (L2 on run level)
        var shaped = new List<(ShapedRun Run, int Level)>();
        foreach (var (s, e, level, script) in runs)
        {
            int utfStart = positions[s], utfEnd = e < positions.Count ? positions[e] : text.Length;
            shaped.Add((ShapeRun(font, text, cpArray, positions, s, e, (level & 1) == 1, script, ligatures, kerning), level));
        }
        var order = Bidi.VisualOrder(shaped.Select(r => (byte)r.Level).ToArray());
        var result = new List<ShapedRun>();
        double x = 0;
        foreach (int index in order)
        {
            var run = shaped[index].Run;
            run.Start = x;
            run.Level = shaped[index].Level;
            x += run.Width;
            result.Add(run);
        }
        return new ShapedLine { Runs = result, Text = text };
    }

    /// <summary>Scripts per character; Common and Inherited characters take the script of their neighbors.</summary>
    private static UnicodeScript[] ResolveScripts(int[] cps)
    {
        var scripts = new UnicodeScript[cps.Length];
        for (int i = 0; i < cps.Length; i++) scripts[i] = UnicodeTables.ScriptOf(cps[i]);
        var last = UnicodeScript.Common;
        for (int i = 0; i < cps.Length; i++)
        {
            if (scripts[i] is UnicodeScript.Common or UnicodeScript.Inherited) scripts[i] = last;
            else last = scripts[i];
        }
        // leading common characters take the first real script
        int first = Array.FindIndex(scripts, s => s != UnicodeScript.Common);
        if (first > 0)
            for (int i = 0; i < first; i++) scripts[i] = scripts[first];
        return scripts;
    }

    private static string[] ScriptTags(UnicodeScript script) => script switch
    {
        UnicodeScript.Arabic => new[] { "arab" },
        UnicodeScript.Hebrew => new[] { "hebr" },
        UnicodeScript.Syriac => new[] { "syrc" },
        UnicodeScript.Thaana => new[] { "thaa" },
        UnicodeScript.Nko => new[] { "nko " },
        UnicodeScript.Latin => new[] { "latn" },
        UnicodeScript.Cyrillic => new[] { "cyrl" },
        UnicodeScript.Greek => new[] { "grek" },
        UnicodeScript.Armenian => new[] { "armn" },
        UnicodeScript.Georgian => new[] { "geor" },
        UnicodeScript.Thai => new[] { "thai" },
        UnicodeScript.Lao => new[] { "lao " },
        UnicodeScript.Hangul => new[] { "hang" },
        UnicodeScript.Han => new[] { "hani" },
        UnicodeScript.Hiragana or UnicodeScript.Katakana => new[] { "kana" },
        _ => Array.Empty<string>(),
    };

    private static bool IsJoiningScript(UnicodeScript script) =>
        script is UnicodeScript.Arabic or UnicodeScript.Syriac or UnicodeScript.Nko or UnicodeScript.Mongolian or UnicodeScript.Mandaic
            or UnicodeScript.Manichaean or UnicodeScript.Psalter_Pahlavi or UnicodeScript.Adlam or UnicodeScript.Hanifi_Rohingya or UnicodeScript.Sogdian;

    private static bool IsHiddenJoiner(ShapingGlyph g) => g.CodePoint is 0x200C or 0x200D && !g.Substituted;

    /// <summary>
    /// The glyph class from GDEF, else from the character (marks). With <paramref name="harfBuzzClasses"/> (Indic
    /// scripts) a font with glyph classes decides alone (unclassified glyphs are never skipped) and without them only
    /// nonspacing marks are marks, like HarfBuzz.
    /// </summary>
    internal static GlyphClass GlyphClassOf(OpenTypeLayout.Gdef? gdef, int gid, int cp, bool harfBuzzClasses = false)
    {
        if (gdef is { HasGlyphClasses: true } && (harfBuzzClasses || gdef.ClassOf(gid) != GlyphClass.Unknown)) return gdef.ClassOf(gid);
        bool mark = harfBuzzClasses
            ? cp <= 0x10FFFF && System.Globalization.CharUnicodeInfo.GetUnicodeCategory(cp) == System.Globalization.UnicodeCategory.NonSpacingMark
            : UnicodeTables.IsMark(cp);
        return mark ? GlyphClass.Mark : GlyphClass.Base;
    }

    private static ShapedRun ShapeRun(TrueTypeFont font, string text, int[] cps, List<int> positions, int start, int end, bool rtl,
        UnicodeScript script, bool ligatures, bool kerning)
    {
        if (IndicShaper.For(font, script) is { } indic)
        {
            // Indic scripts: reordering and basic forms in the Indic shaper; marks keep their advance widths
            var glyphs = indic.Shape(cps, positions, start, end, ligatures);
            foreach (var g in glyphs) g.XAdvance = font.GetAdvanceWidth(g.Gid);
            if (font.Gpos is { } indicGpos)
            {
                var features = new HashSet<string>(StringComparer.Ordinal) { "abvm", "blwm", "dist", "kern", "mark", "mkmk", "curs" };
                foreach (var (lookup, feature) in indicGpos.CollectLookups(indic.GposTags, null, features))
                    indicGpos.Apply(glyphs, lookup, Global, keepMarkAdvance: true, manualJoiners: feature is "mark" or "mkmk");
            }
            else
            {
                var kern = font.LegacyKerning;
                for (int i = 0; i + 1 < glyphs.Count; i++) glyphs[i].XAdvance += kern.Get(glyphs[i].Gid, glyphs[i + 1].Gid);
            }
            // joiners have done their work and are not shown (unless the font substituted them)
            foreach (var g in glyphs)
                if (IsHiddenJoiner(g))
                    g.XAdvance = g.XOffset = g.YOffset = 0;
            return Position(glyphs, text, false, positions, start, end, indicText: true);
        }

        var gdef = font.Gsub?.GlyphDefinitions ?? font.Gpos?.GlyphDefinitions;
        var buffer = new List<ShapingGlyph>(end - start);
        for (int i = start; i < end; i++)
        {
            int cp = cps[i];
            // mirrored characters (brackets) in right-to-left runs
            int shown = rtl ? UnicodeTables.Mirror(cp) : cp;
            int gid = font.GetGlyphId(shown);
            if (gid == 0 && shown != cp) gid = font.GetGlyphId(cp);
            var g = new ShapingGlyph { Gid = gid, Cluster = positions[i], CodePoint = cp, Mask = Global };
            g.Class = gdef is { HasGlyphClasses: true } ? gdef.ClassOf(gid) : UnicodeTables.IsMark(cp) ? GlyphClass.Mark : GlyphClass.Base;
            if (g.Class == GlyphClass.Unknown) g.Class = UnicodeTables.IsMark(cp) ? GlyphClass.Mark : GlyphClass.Base;
            buffer.Add(g);
        }
        if (IsJoiningScript(script)) AssignJoiningForms(buffer);

        var tags = ScriptTags(script);
        bool complex = rtl || IsJoiningScript(script) || script == UnicodeScript.Hebrew;

        // substitution
        if (font.Gsub is { } gsub)
        {
            var features = new HashSet<string>(StringComparer.Ordinal) { "ccmp", "locl", "rlig", "rclt", "calt" };
            if (IsJoiningScript(script)) features.UnionWith(new[] { "isol", "fina", "fin2", "fin3", "medi", "med2", "init", "mset" });
            if (ligatures || complex) features.UnionWith(new[] { "liga", "clig" });
            foreach (var (lookup, feature) in gsub.CollectLookups(tags, null, features))
                gsub.Apply(buffer, lookup, MaskOf(feature));
        }

        // positioning: start from the advance widths
        foreach (var g in buffer) g.XAdvance = font.GetAdvanceWidth(g.Gid);
        if (font.Gpos is { } gpos)
        {
            var features = new HashSet<string>(StringComparer.Ordinal) { "mark", "mkmk", "curs", "dist", "abvm", "blwm" };
            if (kerning || complex) features.Add("kern");
            foreach (var (lookup, feature) in gpos.CollectLookups(tags, null, features))
                gpos.Apply(buffer, lookup, MaskOf(feature));
        }
        else if (kerning || complex)
        {
            // legacy kern table
            var kern = font.LegacyKerning;
            for (int i = 0; i + 1 < buffer.Count; i++)
                buffer[i].XAdvance += kern.Get(buffer[i].Gid, buffer[i + 1].Gid);
        }
        // marks without positioning data: no advance of their own in right-to-left and joining scripts
        foreach (var g in buffer)
            if (g.Class == GlyphClass.Mark && g.AttachTo < 0 && complex) g.XAdvance = 0;

        return Position(buffer, text, rtl, positions, start, end);
    }

    internal static uint MaskOf(string feature) => feature switch
    {
        "isol" => Isol,
        "fina" or "fin2" or "fin3" => Fina,
        "medi" or "med2" => Medi,
        "init" => Init,
        "rphf" => Rphf,
        "pref" => Pref,
        "blwf" => Blwf,
        "abvf" => Abvf,
        "half" => Half,
        "pstf" => Pstf,
        _ => Global,
    };

    /// <summary>The positional form of each letter of a joining script (isolated, initial, medial, final).</summary>
    private static void AssignJoiningForms(List<ShapingGlyph> buffer)
    {
        int n = buffer.Count;
        var types = buffer.Select(g => UnicodeTables.JoiningTypeOf(g.CodePoint)).ToArray();
        bool JoinsForward(JoiningType t) => t is JoiningType.Dual or JoiningType.Left or JoiningType.Causing;   // to the next (left) letter
        bool JoinsBackward(JoiningType t) => t is JoiningType.Dual or JoiningType.Right or JoiningType.Causing; // to the previous (right) letter
        int previous = -1;
        for (int i = 0; i < n; i++)
        {
            var t = types[i];
            if (t == JoiningType.Transparent) continue;
            int next = i + 1;
            while (next < n && types[next] == JoiningType.Transparent) next++;
            bool joinPrevious = previous >= 0 && JoinsForward(types[previous]) && JoinsBackward(t);
            bool joinNext = next < n && JoinsForward(t) && JoinsBackward(types[next]);
            uint form = t is JoiningType.None or JoiningType.Causing ? 0
                : joinPrevious && joinNext ? Medi : joinPrevious ? Fina : joinNext ? Init : Isol;
            buffer[i].Mask = Global | form;
            previous = i;
        }
    }

    /// <summary>
    /// Glyph positions in visual order (font units from the start of the run). In Indic runs attached marks keep their
    /// advance and each glyph carries the characters it stands for (the syllables are reordered, so clusters are whole
    /// syllables).
    /// </summary>
    private static ShapedRun Position(List<ShapingGlyph> buffer, string text, bool rtl, List<int> positions, int start, int end,
        bool indicText = false)
    {
        int n = buffer.Count;
        var x = new double[n];
        var y = new double[n];
        double total = 0;
        foreach (var g in buffer) total += g.XAdvance;
        double pen = 0;
        for (int i = 0; i < n; i++)
        {
            var g = buffer[i];
            if (g.AttachTo >= 0 && g.AttachTo < i)
            {
                x[i] = x[g.AttachTo] + g.XOffset;
                y[i] = y[g.AttachTo] + g.YOffset;
                if (indicText) pen += g.XAdvance;
                continue;
            }
            x[i] = rtl ? total - pen - g.XAdvance + g.XOffset : pen + g.XOffset;
            y[i] = g.YOffset;
            pen += g.XAdvance;
        }
        // the text of each cluster goes to its first glyph (in logical order)
        int utfEnd = end < positions.Count ? positions[end] : text.Length;
        var clusterStarts = buffer.Select(g => g.Cluster).Distinct().OrderBy(c => c).ToList();
        var texts = new string[n];
        var seen = new HashSet<int>();
        for (int i = 0; i < n; i++)
        {
            if (indicText)
            {
                texts[i] = buffer[i].Chars ?? char.ConvertFromUtf32(buffer[i].CodePoint);
                continue;
            }
            int cluster = buffer[i].Cluster;
            if (!seen.Add(cluster))
            {
                texts[i] = "";
                continue;
            }
            int index = clusterStarts.IndexOf(cluster);
            int clusterEnd = index + 1 < clusterStarts.Count ? clusterStarts[index + 1] : utfEnd;
            texts[i] = text[cluster..Math.Max(cluster, clusterEnd)];
        }
        var glyphs = new List<PositionedGlyph>(n);
        for (int k = 0; k < n; k++)
        {
            int i = rtl ? n - 1 - k : k;
            if (indicText && IsHiddenJoiner(buffer[i])) continue;
            glyphs.Add(new PositionedGlyph(buffer[i].Gid, x[i], y[i], buffer[i].Cluster, texts[i]));
        }
        int utfStart = positions[start];
        return new ShapedRun { Glyphs = glyphs, RightToLeft = rtl, Text = text[utfStart..utfEnd], Width = total, LogicalStart = utfStart, LogicalEnd = utfEnd };
    }
}

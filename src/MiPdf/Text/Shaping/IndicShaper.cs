using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MiPdf.Fonts.TrueType;

namespace MiPdf.Text.Shaping;

/// <summary>
/// Shaping of the Indic scripts Devanagari, Bengali, Gurmukhi, Gujarati, Oriya, Tamil, Telugu, Kannada and Malayalam,
/// following Microsoft's OpenType script development specifications and the Indic shaper of HarfBuzz (see NOTICE):
/// the text is split into syllables, the glyphs of each syllable are reordered (pre-base matras before the
/// consonants, reph), the basic forms are applied one feature at a time (nukt, akhn, rphf, rkrf, pref, blwf, abvf,
/// half, pstf, vatu, cjct), then the final reordering moves matras, reph and pre-base forms to their visual
/// positions and the presentation forms follow (init, pres, abvs, blws, psts, haln). New-style fonts (script tags
/// dev2, bng2, ...) and old-style fonts (deva, beng, ...) are supported.
/// </summary>
internal sealed class IndicShaper
{
    private enum RephPosition { AfterMain, BeforeSub, AfterSub, BeforePost, AfterPost }

    private enum RephMode { Implicit, Explicit, LogicalRepha }

    private sealed record Config(int Virama, RephPosition RephPosition, RephMode RephMode, bool BlwfPreAndPost, string NewTag, string OldTag);

    // syllable types (low 4 bits of ShapingGlyph.Syllable)
    private const int ConsonantSyllable = 0, VowelSyllable = 1, StandaloneCluster = 2, SymbolCluster = 3, BrokenCluster = 4, NonIndicCluster = 5;

    private static readonly string[] BasicFeatures = { "nukt", "akhn", "rphf", "rkrf", "pref", "blwf", "abvf", "half", "pstf", "vatu", "cjct" };
    private static readonly string[] PresentationFeatures = { "init", "pres", "abvs", "blws", "psts", "haln", "rlig", "calt", "clig", "rclt" };

    private static readonly ConditionalWeakTable<TrueTypeFont, ConcurrentDictionary<UnicodeScript, IndicShaper?>> Cache = new();

    private readonly TrueTypeFont _font;
    private readonly UnicodeScript _script;
    private readonly Config _config;
    private readonly bool _oldSpec;
    private readonly string[] _gsubTags;
    private readonly int[] _rphf, _pref, _blwf, _pstf, _vatu;
    private readonly bool _hasRphf, _hasPref;
    private readonly bool _zeroContext;
    private readonly int _viramaGlyph;
    private readonly ConcurrentDictionary<int, IndicPosition> _consonantPositions = new();
    private readonly ConcurrentDictionary<string, List<(int Lookup, string Feature)>> _stages = new(StringComparer.Ordinal);

    private IndicShaper(TrueTypeFont font, UnicodeScript script, Config config, bool oldSpec, string[] gsubTags)
    {
        _font = font;
        _script = script;
        _config = config;
        _oldSpec = oldSpec;
        _gsubTags = gsubTags;
        GposTags = new[] { config.NewTag, config.OldTag };
        var gsub = font.Gsub;
        int[] Lookups(string feature) =>
            gsub?.CollectLookups(gsubTags, null, new HashSet<string> { feature }).Select(l => l.Lookup).ToArray() ?? Array.Empty<int>();
        _rphf = Lookups("rphf");
        _pref = Lookups("pref");
        _blwf = Lookups("blwf");
        _pstf = Lookups("pstf");
        _vatu = Lookups("vatu");
        _hasRphf = gsub?.HasFeature(gsubTags, "rphf") == true;
        _hasPref = gsub?.HasFeature(gsubTags, "pref") == true;
        // the forms are looked up without context, except for old-style fonts and Malayalam (like HarfBuzz)
        _zeroContext = !oldSpec && script != UnicodeScript.Malayalam;
        _viramaGlyph = font.GetGlyphId(config.Virama);
    }

    /// <summary>The script tags for GPOS (new style first).</summary>
    public string[] GposTags { get; }

    private static Config? ConfigOf(UnicodeScript script) => script switch
    {
        UnicodeScript.Devanagari => new(0x094D, RephPosition.BeforePost, RephMode.Implicit, true, "dev2", "deva"),
        UnicodeScript.Bengali => new(0x09CD, RephPosition.AfterSub, RephMode.Implicit, true, "bng2", "beng"),
        UnicodeScript.Gurmukhi => new(0x0A4D, RephPosition.BeforeSub, RephMode.Implicit, true, "gur2", "guru"),
        UnicodeScript.Gujarati => new(0x0ACD, RephPosition.BeforePost, RephMode.Implicit, true, "gjr2", "gujr"),
        UnicodeScript.Oriya => new(0x0B4D, RephPosition.AfterMain, RephMode.Implicit, true, "ory2", "orya"),
        UnicodeScript.Tamil => new(0x0BCD, RephPosition.AfterPost, RephMode.Implicit, true, "tml2", "taml"),
        UnicodeScript.Telugu => new(0x0C4D, RephPosition.AfterPost, RephMode.Explicit, false, "tel2", "telu"),
        UnicodeScript.Kannada => new(0x0CCD, RephPosition.AfterPost, RephMode.Implicit, false, "knd2", "knda"),
        UnicodeScript.Malayalam => new(0x0D4D, RephPosition.AfterMain, RephMode.LogicalRepha, true, "mlm2", "mlym"),
        _ => null,
    };

    /// <summary>
    /// The shaper for an Indic script and font, or null for other scripts and for fonts designed for the default
    /// script only (they are shaped without reordering, like HarfBuzz does).
    /// </summary>
    public static IndicShaper? For(TrueTypeFont font, UnicodeScript script)
    {
        if (ConfigOf(script) == null) return null;
        return Cache.GetValue(font, _ => new ConcurrentDictionary<UnicodeScript, IndicShaper?>()).GetOrAdd(script, s => Create(font, s));
    }

    private static IndicShaper? Create(TrueTypeFont font, UnicodeScript script)
    {
        var config = ConfigOf(script)!;
        var gsub = font.Gsub;
        if (gsub == null) return new IndicShaper(font, script, config, oldSpec: true, Array.Empty<string>());
        if (gsub.HasScript(config.NewTag)) return new IndicShaper(font, script, config, oldSpec: false, new[] { config.NewTag });
        if (gsub.HasScript(config.OldTag)) return new IndicShaper(font, script, config, oldSpec: true, new[] { config.OldTag });
        if (gsub.HasScript("DFLT") || gsub.HasScript("latn")) return null;
        return new IndicShaper(font, script, config, oldSpec: true, Array.Empty<string>());
    }

    /// <summary>Shapes the characters [start, end) of a run (GSUB and reordering; positioning follows in the caller).</summary>
    public List<ShapingGlyph> Shape(int[] cps, List<int> positions, int start, int end, bool ligatures)
    {
        var buffer = Normalize(cps, positions, start, end);
        FindSyllables(buffer);
        var gsub = _font.Gsub;
        if (gsub != null) ApplyFeatures(gsub, buffer, "locl", "ccmp");
        InitialReordering(buffer);
        if (gsub != null)
            foreach (var feature in BasicFeatures)
                ApplyFeatures(gsub, buffer, feature);
        foreach (var (s, e) in Syllables(buffer)) FinalReordering(buffer, s, e);
        if (gsub != null) ApplyFeatures(gsub, buffer, ligatures ? PresentationFeatures.Append("liga").ToArray() : PresentationFeatures);
        return buffer;
    }

    private void ApplyFeatures(OpenTypeLayout gsub, List<ShapingGlyph> buffer, params string[] features)
    {
        var lookups = _stages.GetOrAdd(string.Join(",", features),
            _ => gsub.CollectLookups(_gsubTags, null, new HashSet<string>(features, StringComparer.Ordinal)));
        foreach (var (lookup, feature) in lookups)
        {
            // the Indic features are applied per syllable and see joiners; the common features skip them
            bool common = feature is "rlig" or "calt" or "clig" or "liga" or "rclt";
            gsub.Apply(buffer, lookup, Shaper.MaskOf(feature), perSyllable: !common,
                manualJoiners: !common && feature is not ("locl" or "ccmp"));
        }
    }

    // ------------------------------------------------------------------ normalization and syllables

    /// <summary>
    /// Decomposes split matras and nukta letters (when the font has the parts), orders marks canonically and
    /// recomposes letters with nukta that the font has as one glyph; returns the glyph buffer.
    /// </summary>
    private List<ShapingGlyph> Normalize(int[] cps, List<int> positions, int start, int end)
    {
        var input = InsertDottedCirclesInVowelSequences(cps, positions, start, end);
        var chars = new List<(int Cp, int Source)>(input.Count);
        foreach (var (cp, source) in input)
            if (!Decompose(cp, source, chars))
                chars.Add((cp, source));
        // marks are reordered and recomposed only if the text has any (after its first character), like HarfBuzz
        bool anyMark = input.Skip(1).Any(c => IsUnicodeMark(c.Cp));
        if (!anyMark) return CreateGlyphs(chars);
        // canonical ordering of marks (e.g. nukta before virama)
        for (int i = 0; i < chars.Count;)
        {
            if (UnicodeTables.IndicCombiningClass(chars[i].Cp) == 0)
            {
                i++;
                continue;
            }
            int j = i;
            while (j < chars.Count && UnicodeTables.IndicCombiningClass(chars[j].Cp) != 0) j++;
            if (j - i > 1 && j - i <= 32)
            {
                var sorted = chars.GetRange(i, j - i).OrderBy(c => UnicodeTables.IndicCombiningClass(c.Cp)).ToList();
                for (int k = 0; k < sorted.Count; k++) chars[i + k] = sorted[k];
            }
            i = j;
        }
        // recomposition of a mark with its starter (letters with nukta that the font has as one character)
        var composed = new List<(int Cp, int Source)>(chars.Count) { chars[0] };
        int starter = 0;
        for (int i = 1; i < chars.Count; i++)
        {
            var (cp, source) = chars[i];
            if (IsUnicodeMark(cp)
                && (starter == composed.Count - 1 || UnicodeTables.IndicCombiningClass(composed[^1].Cp) < UnicodeTables.IndicCombiningClass(cp)))
            {
                int first = composed[starter].Cp;
                int composite = IsUnicodeMark(first) ? 0 : first == 0x09AF && cp == 0x09BC ? 0x09DF : UnicodeTables.IndicComposition(first, cp);
                if (composite != 0 && _font.GetGlyphId(composite) != 0)
                {
                    composed[starter] = (composite, composed[starter].Source);
                    continue;
                }
            }
            composed.Add((cp, source));
            if (UnicodeTables.IndicCombiningClass(cp) == 0) starter = composed.Count - 1;
        }
        return CreateGlyphs(composed);
    }

    private List<ShapingGlyph> CreateGlyphs(List<(int Cp, int Source)> chars)
    {
        var gdef = _font.Gsub?.GlyphDefinitions ?? _font.Gpos?.GlyphDefinitions;
        var buffer = new List<ShapingGlyph>(chars.Count);
        foreach (var (cp, source) in chars)
        {
            var (category, position) = UnicodeTables.IndicInfo(cp);
            int gid = _font.GetGlyphId(cp);
            buffer.Add(new ShapingGlyph
            {
                Gid = gid, Cluster = source, Source = source, CodePoint = cp, Mask = Shaper.Global, Category = category,
                Position = position, Class = Shaper.GlyphClassOf(gdef, gid, cp, harfBuzzClasses: true),
                Chars = source < 0 ? "" : null,
            });
            if (source < 0) buffer[^1].Cluster = buffer[^1].Source = ~source;
        }
        return buffer;
    }

    private static bool IsUnicodeMark(int cp) => cp <= 0x10FFFF && CharUnicodeInfo.GetUnicodeCategory(cp) is UnicodeCategory.NonSpacingMark
        or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    // independent vowels followed by vowel signs that look like another vowel (Microsoft's list of invalid Indic
    // clusters, as used by HarfBuzz): a dotted circle is shown between them
    private static readonly Dictionary<int, int[]> InvalidVowelSequences = new()
    {
        [0x0905] = new[] { 0x093A, 0x093B, 0x093E, 0x0945, 0x0946, 0x0949, 0x094A, 0x094B, 0x094C, 0x094F, 0x0956, 0x0957 },
        [0x0906] = new[] { 0x093A, 0x0945, 0x0946, 0x0947, 0x0948 },
        [0x0909] = new[] { 0x0941 },
        [0x090F] = new[] { 0x0945, 0x0946, 0x0947 },
        [0x0985] = new[] { 0x09BE },
        [0x098B] = new[] { 0x09C3 },
        [0x098C] = new[] { 0x09E2 },
        [0x0A05] = new[] { 0x0A3E, 0x0A48, 0x0A4C },
        [0x0A72] = new[] { 0x0A3F, 0x0A40, 0x0A47 },
        [0x0A73] = new[] { 0x0A41, 0x0A42, 0x0A4B },
        [0x0A85] = new[] { 0x0ABE, 0x0AC5, 0x0AC7, 0x0AC8, 0x0AC9, 0x0ACB, 0x0ACC },
        [0x0AC5] = new[] { 0x0ABE },
        [0x0B05] = new[] { 0x0B3E },
        [0x0B0F] = new[] { 0x0B57 },
        [0x0B13] = new[] { 0x0B57 },
        [0x0B85] = new[] { 0x0BC2 },
        [0x0C12] = new[] { 0x0C4C, 0x0C55 },
        [0x0C3F] = new[] { 0x0C55 },
        [0x0C46] = new[] { 0x0C55 },
        [0x0C4A] = new[] { 0x0C55 },
        [0x0C89] = new[] { 0x0CBE },
        [0x0C8B] = new[] { 0x0CBE },
        [0x0C92] = new[] { 0x0CCC },
        [0x0D07] = new[] { 0x0D57 },
        [0x0D09] = new[] { 0x0D57 },
        [0x0D0E] = new[] { 0x0D46 },
        [0x0D12] = new[] { 0x0D3E, 0x0D57 },
    };

    /// <summary>
    /// The characters of the run; a dotted circle (source stored as its complement: it stands for no text) is
    /// inserted into invalid vowel sequences and into Devanagari Ra + Virama + I.
    /// </summary>
    private static List<(int Cp, int Source)> InsertDottedCirclesInVowelSequences(int[] cps, List<int> positions, int start, int end)
    {
        var result = new List<(int, int)>(end - start + 2);
        for (int i = start; i < end; i++)
        {
            result.Add((cps[i], positions[i]));
            if (i + 1 >= end) continue;
            bool matched = InvalidVowelSequences.TryGetValue(cps[i], out var seconds) && Array.IndexOf(seconds, cps[i + 1]) >= 0;
            if (cps[i] == 0x0930 && cps[i + 1] == 0x094D && i + 2 < end && cps[i + 2] == 0x0907)
            {
                result.Add((cps[++i], positions[i]));
                matched = true;
            }
            if (!matched) continue;
            // the dotted circle, then the vowel sign (which does not start another sequence)
            result.Add((0x25CC, ~positions[i + 1]));
            i++;
            result.Add((cps[i], positions[i]));
        }
        return result;
    }

    private bool Decompose(int cp, int source, List<(int Cp, int Source)> output)
    {
        if (cp is 0x0931 or 0x09DC or 0x09DD or 0x0B94) return false; // kept composed (like HarfBuzz)
        if (UnicodeTables.IndicDecomposition(cp) is not var (a, b)) return false;
        if (_font.GetGlyphId(b) == 0) return false;
        if (Decompose(a, source, output))
        {
            output.Add((b, source));
            return true;
        }
        if (_font.GetGlyphId(a) == 0) return false;
        output.Add((a, source));
        output.Add((b, source));
        return true;
    }

    private static char Letter(IndicCategory category) => category switch
    {
        IndicCategory.C => 'c',
        IndicCategory.V => 'v',
        IndicCategory.N => 'n',
        IndicCategory.H => 'h',
        IndicCategory.ZWNJ => 'w',
        IndicCategory.ZWJ => 'j',
        IndicCategory.M => 'm',
        IndicCategory.SM => 's',
        IndicCategory.A => 'a',
        IndicCategory.Placeholder => 'p',
        IndicCategory.DottedCircle => 'd',
        IndicCategory.RS => 'r',
        IndicCategory.Repha => 'e',
        IndicCategory.Ra => 'R',
        IndicCategory.CM => 'k',
        IndicCategory.Symbol => 'y',
        IndicCategory.CS => 't',
        _ => 'x',
    };

    // the syllable grammar of HarfBuzz's Indic shaper (one letter per category, see Letter)
    private static readonly Regex[] SyllablePatterns = BuildPatterns();

    private static Regex[] BuildPatterns()
    {
        const string n = "(?:w?r)?(?:nn?)?";
        const string cn = "[cR]j?" + n;
        const string reph = "(?:Rh|e)";
        const string matraGroup = "[jw]*mn?h?";
        const string syllableTail = "(?:[jw]?ss?w?)?a*";
        const string halantGroup = "[jw]?h(?:jn?)?";
        const string finalHalantGroup = "(?:" + halantGroup + "|hw)";
        const string halantOrMatraGroup = "(?:" + finalHalantGroup + "|(?:" + matraGroup + ")*)";
        const string complexTail = "(?:" + halantGroup + cn + ")*k?" + halantOrMatraGroup + syllableTail;
        string[] patterns =
        {
            "[et]?" + cn + complexTail,                                     // consonant syllable
            reph + "?v" + n + "(?:j|" + complexTail + ")",                  // vowel syllable
            "(?:[et]?p|" + reph + "?d)" + n + complexTail,                  // standalone cluster
            "yn?" + syllableTail,                                           // symbol cluster
            reph + "?" + n + complexTail,                                   // broken cluster
        };
        return patterns.Select(p => new Regex("^(?:" + p + ")$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)).ToArray();
    }

    /// <summary>Finds the syllables (longest match, the first pattern on ties) and merges their clusters.</summary>
    private static void FindSyllables(List<ShapingGlyph> buffer)
    {
        var letters = buffer.Select(g => Letter(g.Category)).ToArray();
        int serial = 0;
        for (int i = 0; i < letters.Length;)
        {
            int limit = i;
            while (limit < letters.Length && letters[limit] != 'x' && limit - i < 48) limit++;
            int length = 1, type = NonIndicCluster;
            for (int l = limit - i; l >= 1 && type == NonIndicCluster; l--)
                for (int p = 0; p < SyllablePatterns.Length; p++)
                    if (SyllablePatterns[p].IsMatch(letters.AsSpan(i, l)))
                    {
                        length = l;
                        type = p;
                        break;
                    }
            int syllable = (++serial << 4) | type;
            int cluster = int.MaxValue;
            for (int k = i; k < i + length; k++) cluster = Math.Min(cluster, buffer[k].Cluster);
            for (int k = i; k < i + length; k++)
            {
                buffer[k].Syllable = syllable;
                buffer[k].Cluster = cluster;
            }
            i += length;
        }
    }

    private static List<(int Start, int End)> Syllables(List<ShapingGlyph> buffer)
    {
        var result = new List<(int, int)>();
        for (int i = 0; i < buffer.Count;)
        {
            int j = i + 1;
            while (j < buffer.Count && buffer[j].Syllable == buffer[i].Syllable) j++;
            result.Add((i, j));
            i = j;
        }
        return result;
    }

    // ------------------------------------------------------------------ helpers

    private static uint F(IndicCategory category) => 1u << (int)category;

    private const uint ConsonantFlags = (1u << (int)IndicCategory.C) | (1u << (int)IndicCategory.CS) | (1u << (int)IndicCategory.Ra)
        | (1u << (int)IndicCategory.CM) | (1u << (int)IndicCategory.V) | (1u << (int)IndicCategory.Placeholder)
        | (1u << (int)IndicCategory.DottedCircle);

    private const uint JoinerFlags = (1u << (int)IndicCategory.ZWJ) | (1u << (int)IndicCategory.ZWNJ);

    /// <summary>The category test of HarfBuzz: a glyph that ligated has no category any more.</summary>
    private static bool IsOneOf(ShapingGlyph g, uint flags) => !g.Ligated && (flags & F(g.Category)) != 0;

    private static bool IsConsonant(ShapingGlyph g) => IsOneOf(g, ConsonantFlags);

    private static bool IsHalant(ShapingGlyph g) => IsOneOf(g, F(IndicCategory.H));

    private static bool IsJoiner(ShapingGlyph g) => IsOneOf(g, JoinerFlags);

    private bool Would(int[] lookups, params int[] glyphs) => lookups.Length > 0 && _font.Gsub!.WouldSubstitute(lookups, _zeroContext, glyphs);

    /// <summary>Below-base, post-base or base: depends on the forms the font has for the consonant with a virama.</summary>
    private IndicPosition ConsonantPosition(int consonant) => _consonantPositions.GetOrAdd(consonant, c =>
    {
        int v = _viramaGlyph;
        // new-style fonts use virama + consonant, old-style fonts consonant + virama; some fonts mix them
        if (Would(_blwf, v, c) || Would(_blwf, c, v) || Would(_vatu, v, c) || Would(_vatu, c, v)) return IndicPosition.BelowC;
        if (Would(_pstf, v, c) || Would(_pstf, c, v)) return IndicPosition.PostC;
        if (Would(_pref, v, c) || Would(_pref, c, v)) return IndicPosition.PostC;
        return IndicPosition.BaseC;
    });

    // ------------------------------------------------------------------ initial reordering

    private void InitialReordering(List<ShapingGlyph> buffer)
    {
        if (_viramaGlyph != 0)
            foreach (var g in buffer)
                if (g.Position == IndicPosition.BaseC)
                    g.Position = ConsonantPosition(g.Gid);
        InsertDottedCircles(buffer);
        foreach (var (start, end) in Syllables(buffer))
            if ((buffer[start].Syllable & 0xF) is ConsonantSyllable or VowelSyllable or StandaloneCluster or BrokenCluster)
                ReorderSyllable(buffer, start, end);
    }

    /// <summary>Broken clusters (e.g. a matra without consonant) are shown on a dotted circle, after a leading repha.</summary>
    private void InsertDottedCircles(List<ShapingGlyph> buffer)
    {
        int dotted = _font.GetGlyphId(0x25CC);
        if (dotted == 0) return;
        var gdef = _font.Gsub?.GlyphDefinitions ?? _font.Gpos?.GlyphDefinitions;
        int last = -1;
        for (int i = 0; i < buffer.Count; i++)
        {
            int syllable = buffer[i].Syllable;
            if (syllable == last || (syllable & 0xF) != BrokenCluster) continue;
            last = syllable;
            var first = buffer[i];
            var circle = new ShapingGlyph
            {
                Gid = dotted, Cluster = first.Cluster, Source = first.Source, CodePoint = 0x25CC, Mask = first.Mask,
                Syllable = syllable, Category = IndicCategory.DottedCircle, Position = IndicPosition.End, Chars = "",
                Class = Shaper.GlyphClassOf(gdef, dotted, 0x25CC, harfBuzzClasses: true),
            };
            int at = i;
            while (at < buffer.Count && buffer[at].Syllable == syllable && buffer[at].Category == IndicCategory.Repha) at++;
            buffer.Insert(at, circle);
            i = at;
        }
    }

    private void ReorderSyllable(List<ShapingGlyph> b, int start, int end)
    {
        // Kannada: Ra,Halant,ZWJ behaves like Ra,ZWJ,Halant
        if (_script == UnicodeScript.Kannada && start + 3 <= end && IsOneOf(b[start], F(IndicCategory.Ra))
            && IsOneOf(b[start + 1], F(IndicCategory.H)) && IsOneOf(b[start + 2], F(IndicCategory.ZWJ)))
            (b[start + 1], b[start + 2]) = (b[start + 2], b[start + 1]);

        // 1. the base consonant: from the end, the first consonant without a below-base or post-base form; a
        //    syllable starting with Ra + Halant (with a reph form) excludes the Ra
        int baseIndex = end;
        bool hasReph = false;
        int limit = start;
        if (_hasRphf && start + 3 <= end
            && ((_config.RephMode == RephMode.Implicit && !IsJoiner(b[start + 2]))
                || (_config.RephMode == RephMode.Explicit && b[start + 2].Category == IndicCategory.ZWJ)))
        {
            if (Would(_rphf, b[start].Gid, b[start + 1].Gid)
                || (_config.RephMode == RephMode.Explicit && Would(_rphf, b[start].Gid, b[start + 1].Gid, b[start + 2].Gid)))
            {
                limit += 2;
                while (limit < end && IsJoiner(b[limit])) limit++;
                baseIndex = start;
                hasReph = true;
            }
        }
        else if (_config.RephMode == RephMode.LogicalRepha && b[start].Category == IndicCategory.Repha)
        {
            limit += 1;
            while (limit < end && IsJoiner(b[limit])) limit++;
            baseIndex = start;
            hasReph = true;
        }

        {
            int i = end;
            bool seenBelow = false;
            do
            {
                i--;
                if (IsConsonant(b[i]))
                {
                    if (b[i].Position != IndicPosition.BelowC && (b[i].Position != IndicPosition.PostC || seenBelow))
                    {
                        baseIndex = i;
                        break;
                    }
                    if (b[i].Position == IndicPosition.BelowC) seenBelow = true;
                    baseIndex = i;
                }
                else if (start < i && b[i].Category == IndicCategory.ZWJ && b[i - 1].Category == IndicCategory.H)
                {
                    // a ZWJ after a halant stops the search (explicit half form)
                    break;
                }
            } while (i > limit);
        }
        // a reph needs another consonant; otherwise the Ra is the base
        if (hasReph && baseIndex == start && limit - baseIndex <= 2) hasReph = false;

        // 2. positions of the parts
        for (int i = start; i < baseIndex; i++)
            if (b[i].Position > IndicPosition.PreC) b[i].Position = IndicPosition.PreC;
        if (baseIndex < end) b[baseIndex].Position = IndicPosition.BaseC;
        if (hasReph) b[start].Position = IndicPosition.RaToBecomeReph;

        // old-style fonts: the first post-base halant moves after the last consonant
        if (_oldSpec)
        {
            bool disallowDoubleHalants = _script == UnicodeScript.Kannada;
            for (int i = baseIndex + 1; i < end; i++)
                if (b[i].Category == IndicCategory.H)
                {
                    int j;
                    for (j = end - 1; j > i; j--)
                        if (IsConsonant(b[j]) || (disallowDoubleHalants && b[j].Category == IndicCategory.H)) break;
                    if (b[j].Category != IndicCategory.H && j > i)
                    {
                        var t = b[i];
                        b.RemoveAt(i);
                        b.Insert(j, t);
                    }
                    break;
                }
        }

        // joiners, nukta, register shifters, medials and halants move with the preceding character
        {
            var lastPos = IndicPosition.Start;
            const uint attached = JoinerFlags | (1u << (int)IndicCategory.N) | (1u << (int)IndicCategory.RS)
                | (1u << (int)IndicCategory.CM) | (1u << (int)IndicCategory.H);
            for (int i = start; i < end; i++)
            {
                if ((F(b[i].Category) & attached) != 0)
                {
                    b[i].Position = lastPos;
                    if (b[i].Category == IndicCategory.H && b[i].Position == IndicPosition.PreM)
                    {
                        // a halant does not move with a left matra
                        for (int j = i; j > start; j--)
                            if (b[j - 1].Position != IndicPosition.PreM)
                            {
                                b[i].Position = b[j - 1].Position;
                                break;
                            }
                    }
                }
                else if (b[i].Position != IndicPosition.Smvd)
                {
                    lastPos = b[i].Position;
                }
            }
        }
        // post-base consonants own everything since the last consonant or matra
        {
            int last = baseIndex;
            for (int i = baseIndex + 1; i < end; i++)
            {
                if (IsConsonant(b[i]))
                {
                    for (int j = last + 1; j < i; j++)
                        if (b[j].Position < IndicPosition.Smvd) b[j].Position = b[i].Position;
                    last = i;
                }
                else if (b[i].Category == IndicCategory.M)
                {
                    last = i;
                }
            }
        }

        // 3. reorder (stable)
        var sorted = b.GetRange(start, end - start).OrderBy(g => g.Position).ToList();
        for (int k = 0; k < sorted.Count; k++) b[start + k] = sorted[k];

        // find the base again; several left matras keep their logical order
        int firstLeftMatra = end, lastLeftMatra = end;
        baseIndex = end;
        for (int i = start; i < end; i++)
        {
            if (b[i].Position == IndicPosition.BaseC)
            {
                baseIndex = i;
                break;
            }
            if (b[i].Position == IndicPosition.PreM)
            {
                if (firstLeftMatra == end) firstLeftMatra = i;
                lastLeftMatra = i;
            }
        }
        if (firstLeftMatra < lastLeftMatra)
        {
            b.Reverse(firstLeftMatra, lastLeftMatra - firstLeftMatra + 1);
            int i = firstLeftMatra;
            for (int j = i; j <= lastLeftMatra; j++)
                if (b[j].Category == IndicCategory.M)
                {
                    b.Reverse(i, j - i + 1);
                    i = j + 1;
                }
        }

        // 4. feature masks: reph, pre-base (half forms), post-base (below, above and post-base forms)
        for (int i = start; i < end && b[i].Position == IndicPosition.RaToBecomeReph; i++) b[i].Mask |= Shaper.Rphf;
        uint preBase = Shaper.Half | (!_oldSpec && _config.BlwfPreAndPost ? Shaper.Blwf : 0);
        for (int i = start; i < baseIndex; i++) b[i].Mask |= preBase;
        for (int i = baseIndex + 1; i < end; i++) b[i].Mask |= Shaper.Blwf | Shaper.Abvf | Shaper.Pstf;
        if (_oldSpec && _script == UnicodeScript.Devanagari)
        {
            // old-style eyelash Ra: Ra + Halant before the base takes the below-base form
            for (int i = start; i + 1 < baseIndex; i++)
                if (b[i].Category == IndicCategory.Ra && b[i + 1].Category == IndicCategory.H
                    && (i + 2 == baseIndex || b[i + 2].Category != IndicCategory.ZWJ))
                {
                    b[i].Mask |= Shaper.Blwf;
                    b[i + 1].Mask |= Shaper.Blwf;
                }
        }
        if (_hasPref && baseIndex + 2 < end)
        {
            // a Halant + Ra (pre-base reordering form) after the base
            for (int i = baseIndex + 1; i + 1 < end; i++)
                if (Would(_pref, b[i].Gid, b[i + 1].Gid))
                {
                    b[i].Mask |= Shaper.Pref;
                    b[i + 1].Mask |= Shaper.Pref;
                    break;
                }
        }
        // a ZWNJ prevents the half forms of the preceding consonant
        for (int i = start + 1; i < end; i++)
            if (IsJoiner(b[i]))
            {
                bool nonJoiner = b[i].Category == IndicCategory.ZWNJ;
                int j = i;
                do
                {
                    j--;
                    if (nonJoiner) b[j].Mask &= ~Shaper.Half;
                } while (j > start && !IsConsonant(b[j]));
            }
    }

    // ------------------------------------------------------------------ final reordering

    private void FinalReordering(List<ShapingGlyph> b, int start, int end)
    {
        // a virama that was decomposed from a ligature is a halant again
        if (_viramaGlyph != 0)
            for (int i = start; i < end; i++)
                if (b[i].Gid == _viramaGlyph && b[i].Ligated && b[i].Multiplied)
                {
                    b[i].Category = IndicCategory.H;
                    b[i].Ligated = b[i].Multiplied = false;
                }

        bool tryPref = _hasPref;
        int baseIndex;
        for (baseIndex = start; baseIndex < end; baseIndex++)
        {
            if (b[baseIndex].Position < IndicPosition.BaseC) continue;
            if (tryPref && baseIndex + 1 < end)
            {
                for (int i = baseIndex + 1; i < end; i++)
                    if ((b[i].Mask & Shaper.Pref) != 0)
                    {
                        if (!(b[i].Substituted && b[i].Ligated && !b[i].Multiplied))
                        {
                            // a pre-base form candidate that did not form: the base is around here
                            baseIndex = i;
                            while (baseIndex < end && IsHalant(b[baseIndex])) baseIndex++;
                            if (baseIndex < end) b[baseIndex].Position = IndicPosition.BaseC;
                            tryPref = false;
                        }
                        break;
                    }
                if (baseIndex == end) break;
            }
            if (_script == UnicodeScript.Malayalam)
            {
                // unformed below-base forms (not post-base forms) are skipped
                for (int i = baseIndex + 1; i < end; i++)
                {
                    while (i < end && IsJoiner(b[i])) i++;
                    if (i == end || !IsHalant(b[i])) break;
                    i++;
                    while (i < end && IsJoiner(b[i])) i++;
                    if (i < end && IsConsonant(b[i]) && b[i].Position == IndicPosition.BelowC)
                    {
                        baseIndex = i;
                        b[baseIndex].Position = IndicPosition.BaseC;
                    }
                }
            }
            if (start < baseIndex && b[baseIndex].Position > IndicPosition.BaseC) baseIndex--;
            break;
        }
        if (baseIndex == end && start < baseIndex && IsOneOf(b[baseIndex - 1], F(IndicCategory.ZWJ))) baseIndex--;
        if (baseIndex < end)
            while (start < baseIndex && IsOneOf(b[baseIndex], F(IndicCategory.N) | F(IndicCategory.H)))
                baseIndex--;

        // pre-base matras move closer to the base: after the last halant (that is not part of the matra and not
        // followed by a ZWJ) before the base
        if (start + 1 < end && start < baseIndex)
        {
            int newPos = baseIndex == end ? baseIndex - 2 : baseIndex - 1;
            if (_script != UnicodeScript.Malayalam && _script != UnicodeScript.Tamil)
            {
                while (true)
                {
                    while (newPos > start && !IsOneOf(b[newPos], F(IndicCategory.M) | F(IndicCategory.H))) newPos--;
                    if (IsHalant(b[newPos]) && b[newPos].Position != IndicPosition.PreM)
                    {
                        if (newPos + 1 < end && b[newPos + 1].Category == IndicCategory.ZWJ && newPos > start)
                        {
                            newPos--;
                            continue;
                        }
                    }
                    else
                    {
                        newPos = start;
                    }
                    break;
                }
            }
            if (start < newPos && b[newPos].Position != IndicPosition.PreM)
            {
                for (int i = newPos; i > start; i--)
                    if (b[i - 1].Position == IndicPosition.PreM)
                    {
                        int oldPos = i - 1;
                        if (oldPos < baseIndex && baseIndex <= newPos) baseIndex--;
                        var tmp = b[oldPos];
                        b.RemoveAt(oldPos);
                        b.Insert(newPos, tmp);
                        newPos--;
                    }
            }
        }

        // reph: moves only if Ra + Halant ligated to the reph form (or a logical repha did not ligate)
        if (start + 1 < end && b[start].Position == IndicPosition.RaToBecomeReph
            && ((b[start].Category == IndicCategory.Repha) ^ (b[start].Ligated && !b[start].Multiplied)))
        {
            var rephPos = _config.RephPosition;
            int newRephPos = start;
            bool found = false;

            bool AfterFirstHalant()
            {
                // after the first explicit halant between the reph and the base (and a following joiner)
                newRephPos = start + 1;
                while (newRephPos < baseIndex && !IsHalant(b[newRephPos])) newRephPos++;
                if (newRephPos < baseIndex && IsHalant(b[newRephPos]))
                {
                    if (newRephPos + 1 < baseIndex && IsJoiner(b[newRephPos + 1])) newRephPos++;
                    return true;
                }
                return false;
            }

            if (rephPos != RephPosition.AfterPost)
            {
                found = AfterFirstHalant();
                if (!found && rephPos == RephPosition.AfterMain)
                {
                    newRephPos = baseIndex;
                    while (newRephPos + 1 < end && b[newRephPos + 1].Position <= IndicPosition.AfterMain) newRephPos++;
                    found = newRephPos < end;
                }
                if (!found && rephPos == RephPosition.AfterSub)
                {
                    newRephPos = baseIndex;
                    while (newRephPos + 1 < end
                           && b[newRephPos + 1].Position is not (IndicPosition.PostC or IndicPosition.AfterPost or IndicPosition.Smvd))
                        newRephPos++;
                    found = newRephPos < end;
                }
            }
            if (!found) found = AfterFirstHalant();
            if (!found)
            {
                // at the end of the syllable, before syllable modifiers and vedic signs
                newRephPos = end - 1;
                while (newRephPos > start && b[newRephPos].Position == IndicPosition.Smvd) newRephPos--;
                // after a matra + halant: before the halant, so that it can interact with the matra
                if (IsHalant(b[newRephPos]))
                    for (int i = baseIndex + 1; i < newRephPos; i++)
                        if (b[i].Category == IndicCategory.M) newRephPos--;
            }
            var reph = b[start];
            b.RemoveAt(start);
            b.Insert(newRephPos, reph);
            if (start < baseIndex && baseIndex <= newRephPos) baseIndex--;
        }

        // pre-base reordering forms (e.g. Malayalam Ra) move before the base, like pre-base matras
        if (tryPref && baseIndex + 1 < end)
        {
            for (int i = baseIndex + 1; i < end; i++)
                if ((b[i].Mask & Shaper.Pref) != 0)
                {
                    if (b[i].Ligated && !b[i].Multiplied)
                    {
                        int newPos = baseIndex;
                        if (_script != UnicodeScript.Malayalam && _script != UnicodeScript.Tamil)
                            while (newPos > start && !IsOneOf(b[newPos - 1], F(IndicCategory.M) | F(IndicCategory.H))) newPos--;
                        if (newPos > start && IsHalant(b[newPos - 1]) && newPos < end && IsJoiner(b[newPos])) newPos++;
                        int oldPos = i;
                        var tmp = b[oldPos];
                        b.RemoveAt(oldPos);
                        b.Insert(newPos, tmp);
                        if (newPos <= baseIndex && baseIndex < oldPos) baseIndex++;
                    }
                    break;
                }
        }

        // 'init' applies to a left matra at the start of a word
        if (b[start].Position == IndicPosition.PreM && (start == 0 || !IsWordCharacter(b[start - 1].CodePoint)))
            b[start].Mask |= Shaper.Init;
    }

    /// <summary>Letters, marks and format, unassigned, private-use and surrogate characters continue a word.</summary>
    private static bool IsWordCharacter(int cp) => cp <= 0x10FFFF && CharUnicodeInfo.GetUnicodeCategory(cp) is UnicodeCategory.Format
        or UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate or UnicodeCategory.LowercaseLetter
        or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.UppercaseLetter or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
        or UnicodeCategory.NonSpacingMark;
}

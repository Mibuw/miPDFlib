using System.Buffers.Binary;

namespace MiPdf.Fonts.TrueType;

/// <summary>A glyph in the shaping buffer (logical order).</summary>
internal sealed class ShapingGlyph
{
    public int Gid;
    public int Cluster;            // index of the first UTF-16 code unit of the character(s) it shows
    public int CodePoint;          // the original character (before substitution)
    public uint Mask = uint.MaxValue; // features enabled for this glyph (bit per feature, see Shaper)
    public GlyphClass Class;
    public int LigatureId;         // ligature this glyph belongs to (components and marks), 0 if none
    public int LigatureComponent;  // for marks: the component of the ligature they belong to (1-based), 0 = last
    public int ComponentCount = 1; // for ligatures: the number of components
    public bool Substituted;

    // positioning (font units)
    public int XAdvance, YAdvance, XOffset, YOffset;
    public int AttachTo = -1;      // index of the glyph a mark (or cursive glyph) is attached to
    public bool CursiveAttached;

    public ShapingGlyph Clone() => (ShapingGlyph)MemberwiseClone();
}

internal enum GlyphClass : byte { Unknown = 0, Base = 1, Ligature = 2, Mark = 3, Component = 4 }

/// <summary>
/// OpenType layout tables (GSUB, GPOS with GDEF): scripts, language systems, features and lookups, applied to a
/// glyph buffer. Implements GSUB lookup types 1-8 and GPOS lookup types 1-9 (device tables are ignored).
/// </summary>
internal sealed class OpenTypeLayout
{
    private readonly byte[] _d;
    private readonly bool _gpos;
    private readonly int _scriptList, _featureList, _lookupList;
    private readonly Gdef? _gdef;

    private OpenTypeLayout(byte[] data, bool gpos, Gdef? gdef)
    {
        _d = data;
        _gpos = gpos;
        _gdef = gdef;
        _scriptList = U16(4);
        _featureList = U16(6);
        _lookupList = U16(8);
    }

    public static OpenTypeLayout? Load(TrueTypeFont font, bool gpos)
    {
        var table = font.GetTable(gpos ? "GPOS" : "GSUB");
        if (table.Length < 10) return null;
        try
        {
            var gdefTable = font.GetTable("GDEF");
            var gdef = gdefTable.Length >= 12 ? new Gdef(gdefTable.ToArray()) : null;
            return new OpenTypeLayout(table.ToArray(), gpos, gdef);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    public Gdef? GlyphDefinitions => _gdef;

    private int U16(int pos) => pos >= 0 && pos + 2 <= _d.Length ? BinaryPrimitives.ReadUInt16BigEndian(_d.AsSpan(pos)) : 0;

    private short S16(int pos) => pos >= 0 && pos + 2 <= _d.Length ? BinaryPrimitives.ReadInt16BigEndian(_d.AsSpan(pos)) : (short)0;

    private uint U32(int pos) => pos >= 0 && pos + 4 <= _d.Length ? BinaryPrimitives.ReadUInt32BigEndian(_d.AsSpan(pos)) : 0;

    private string Tag(int pos) => pos + 4 <= _d.Length ? System.Text.Encoding.ASCII.GetString(_d, pos, 4) : "";

    // ------------------------------------------------------------------ scripts and features

    /// <summary>True if the font has the script (an OpenType script tag such as "arab").</summary>
    public bool HasScript(string script)
    {
        int count = U16(_scriptList);
        for (int i = 0; i < count; i++)
            if (Tag(_scriptList + 2 + 6 * i) == script) return true;
        return false;
    }

    /// <summary>
    /// The lookups of the requested features for a script (first of the candidates the font has, else DFLT) and
    /// language, each with the feature tag that enabled it, in lookup order (as they must be applied).
    /// </summary>
    public List<(int Lookup, string Feature)> CollectLookups(IEnumerable<string> scriptCandidates, string? language, ISet<string> features)
    {
        int langSys = -1;
        int scriptCount = U16(_scriptList);
        foreach (var candidate in scriptCandidates.Append("DFLT").Append("latn"))
        {
            for (int i = 0; i < scriptCount && langSys < 0; i++)
            {
                if (Tag(_scriptList + 2 + 6 * i) != candidate) continue;
                int script = _scriptList + U16(_scriptList + 2 + 6 * i + 4);
                if (language != null)
                {
                    int langCount = U16(script + 2);
                    for (int l = 0; l < langCount; l++)
                        if (Tag(script + 4 + 6 * l) == language) langSys = script + U16(script + 4 + 6 * l + 4);
                }
                if (langSys < 0 && U16(script) != 0) langSys = script + U16(script);
                if (langSys < 0 && U16(script + 2) > 0) langSys = script + U16(script + 4 + 4); // first language
            }
            if (langSys >= 0) break;
        }
        var result = new SortedDictionary<int, string>();
        if (langSys < 0) return new List<(int, string)>();
        var featureIndices = new List<int>();
        int required = U16(langSys + 2);
        if (required != 0xFFFF) featureIndices.Add(required);
        int featureCount = U16(langSys + 4);
        for (int i = 0; i < featureCount; i++) featureIndices.Add(U16(langSys + 6 + 2 * i));
        int totalFeatures = U16(_featureList);
        foreach (int index in featureIndices)
        {
            if (index >= totalFeatures) continue;
            string tag = Tag(_featureList + 2 + 6 * index);
            if (!features.Contains(tag)) continue;
            int feature = _featureList + U16(_featureList + 2 + 6 * index + 4);
            int lookupCount = U16(feature + 2);
            for (int k = 0; k < lookupCount; k++) result.TryAdd(U16(feature + 4 + 2 * k), tag);
        }
        return result.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    // ------------------------------------------------------------------ lookups

    private const int RightToLeft = 1, IgnoreBase = 2, IgnoreLigatures = 4, IgnoreMarks = 8, UseMarkFilteringSet = 0x10;

    private readonly record struct Lookup(int Type, int Flag, int[] Subtables, int MarkSet);

    private Lookup GetLookup(int index)
    {
        if (index >= U16(_lookupList)) return new Lookup(0, 0, Array.Empty<int>(), 0);
        int lookup = _lookupList + U16(_lookupList + 2 + 2 * index);
        int type = U16(lookup), flag = U16(lookup + 2), count = U16(lookup + 4);
        var subtables = new int[count];
        bool extension = type == (_gpos ? 9 : 7);
        for (int i = 0; i < count; i++)
        {
            int sub = lookup + U16(lookup + 6 + 2 * i);
            // extension lookups (GSUB 7, GPOS 9) point to subtables of the real type
            subtables[i] = extension && U16(sub) == 1 ? sub + (int)U32(sub + 4) : sub;
        }
        if (extension && count > 0) type = U16(lookup + U16(lookup + 6) + 2);
        int markSet = (flag & UseMarkFilteringSet) != 0 ? U16(lookup + 6 + 2 * count) : -1;
        return new Lookup(type, flag, subtables, markSet);
    }

    private bool Ignored(ShapingGlyph g, Lookup lookup)
    {
        int flag = lookup.Flag;
        var cls = g.Class;
        if (cls == GlyphClass.Base && (flag & IgnoreBase) != 0) return true;
        if (cls == GlyphClass.Ligature && (flag & IgnoreLigatures) != 0) return true;
        if (cls == GlyphClass.Mark)
        {
            if ((flag & IgnoreMarks) != 0) return true;
            if (lookup.MarkSet >= 0 && _gdef != null && !_gdef.InMarkSet(lookup.MarkSet, g.Gid)) return true;
            int attachType = flag >> 8;
            if (attachType != 0 && _gdef != null && _gdef.MarkAttachClass(g.Gid) != attachType) return true;
        }
        return false;
    }

    private int Next(List<ShapingGlyph> buffer, int i, Lookup lookup)
    {
        for (int j = i + 1; j < buffer.Count; j++)
            if (!Ignored(buffer[j], lookup)) return j;
        return -1;
    }

    private int Previous(List<ShapingGlyph> buffer, int i, Lookup lookup)
    {
        for (int j = i - 1; j >= 0; j--)
            if (!Ignored(buffer[j], lookup)) return j;
        return -1;
    }

    /// <summary>Applies the lookup to the buffer; only glyphs with <paramref name="featureBit"/> in their mask take part.</summary>
    public void Apply(List<ShapingGlyph> buffer, int lookupIndex, uint featureBit)
    {
        var lookup = GetLookup(lookupIndex);
        if (lookup.Subtables.Length == 0) return;
        if (!_gpos && lookup.Type == 8)
        {
            // reverse chaining: from the end
            for (int i = buffer.Count - 1; i >= 0; i--)
                if ((buffer[i].Mask & featureBit) != 0 && !Ignored(buffer[i], lookup))
                    foreach (int sub in lookup.Subtables)
                        if (ReverseChain(buffer, i, sub, lookup)) break;
            return;
        }
        int pos = 0;
        int guard = buffer.Count * 8 + 64;
        while (pos < buffer.Count && guard-- > 0)
        {
            var g = buffer[pos];
            if ((g.Mask & featureBit) == 0 || Ignored(g, lookup))
            {
                pos++;
                continue;
            }
            int next = -1;
            foreach (int sub in lookup.Subtables)
            {
                next = ApplySubtable(buffer, pos, lookup, sub, 0);
                if (next >= 0) break;
            }
            pos = next >= 0 ? Math.Max(next, pos + 1) : pos + 1;
        }
    }

    /// <summary>Applies one subtable at <paramref name="pos"/>; returns the position to continue at, or -1.</summary>
    private int ApplySubtable(List<ShapingGlyph> buffer, int pos, Lookup lookup, int sub, int depth)
    {
        if (depth > 8) return -1;
        int format = U16(sub);
        int gid = buffer[pos].Gid;
        if (!_gpos)
        {
            switch (lookup.Type)
            {
                case 1: // single
                {
                    int ci = Coverage(sub + U16(sub + 2), gid);
                    if (ci < 0) return -1;
                    int replacement = format == 1 ? (gid + S16(sub + 4)) & 0xFFFF : ci < U16(sub + 4) ? U16(sub + 6 + 2 * ci) : -1;
                    if (replacement < 0) return -1;
                    Replace(buffer[pos], replacement);
                    return pos + 1;
                }
                case 2: // multiple
                {
                    int ci = Coverage(sub + U16(sub + 2), gid);
                    if (ci < 0 || ci >= U16(sub + 4)) return -1;
                    int seq = sub + U16(sub + 6 + 2 * ci);
                    int count = U16(seq);
                    var template = buffer[pos];
                    if (count == 0)
                    {
                        buffer.RemoveAt(pos);
                        return pos;
                    }
                    Replace(template, U16(seq + 2));
                    for (int k = 1; k < count; k++)
                    {
                        var extra = template.Clone();
                        extra.Gid = U16(seq + 2 + 2 * k);
                        extra.Class = _gdef?.ClassOf(extra.Gid) ?? template.Class;
                        buffer.Insert(pos + k, extra);
                    }
                    return pos + count;
                }
                case 3: // alternate: the first alternate
                {
                    int ci = Coverage(sub + U16(sub + 2), gid);
                    if (ci < 0 || ci >= U16(sub + 4)) return -1;
                    int set = sub + U16(sub + 6 + 2 * ci);
                    if (U16(set) == 0) return -1;
                    Replace(buffer[pos], U16(set + 2));
                    return pos + 1;
                }
                case 4: // ligature
                    return Ligature(buffer, pos, lookup, sub);
                case 5:
                    return Context(buffer, pos, lookup, sub, chained: false, depth);
                case 6:
                    return Context(buffer, pos, lookup, sub, chained: true, depth);
            }
            return -1;
        }
        switch (lookup.Type)
        {
            case 1: // single adjustment
            {
                int ci = Coverage(sub + U16(sub + 2), gid);
                if (ci < 0) return -1;
                int valueFormat = U16(sub + 4);
                int record = format == 1 ? sub + 6 : sub + 8 + ci * ValueSize(valueFormat);
                ApplyValue(buffer[pos], sub, record, valueFormat);
                return pos + 1;
            }
            case 2:
                return Pair(buffer, pos, lookup, sub);
            case 3:
                return Cursive(buffer, pos, lookup, sub);
            case 4:
            case 5:
            case 6:
                return MarkAttachment(buffer, pos, lookup, sub, lookup.Type);
            case 7:
                return Context(buffer, pos, lookup, sub, chained: false, depth);
            case 8:
                return Context(buffer, pos, lookup, sub, chained: true, depth);
        }
        return -1;
    }

    private void Replace(ShapingGlyph g, int gid)
    {
        g.Gid = gid;
        g.Substituted = true;
        if (_gdef != null && _gdef.HasGlyphClasses) g.Class = _gdef.ClassOf(gid);
    }

    // ------------------------------------------------------------------ GSUB 4: ligatures

    private int _nextLigatureId = 1;

    private int Ligature(List<ShapingGlyph> buffer, int pos, Lookup lookup, int sub)
    {
        int ci = Coverage(sub + U16(sub + 2), buffer[pos].Gid);
        if (ci < 0 || ci >= U16(sub + 4)) return -1;
        int set = sub + U16(sub + 6 + 2 * ci);
        int ligCount = U16(set);
        for (int l = 0; l < ligCount; l++)
        {
            int lig = set + U16(set + 2 + 2 * l);
            int ligGlyph = U16(lig), components = U16(lig + 2);
            var matched = new List<int> { pos };
            int j = pos;
            bool ok = true;
            for (int c = 1; c < components; c++)
            {
                j = Next(buffer, j, lookup);
                if (j < 0 || buffer[j].Gid != U16(lig + 4 + 2 * (c - 1)))
                {
                    ok = false;
                    break;
                }
                matched.Add(j);
            }
            if (!ok) continue;
            // the ligature replaces the first component; the others are removed, skipped marks stay and are
            // associated with the component they follow (for mark-to-ligature positioning)
            int id = Interlocked.Increment(ref _nextLigatureId);
            var first = buffer[pos];
            int cluster = matched.Min(m => buffer[m].Cluster);
            int component = 1;
            for (int x = pos + 1; x < matched[^1]; x++)
            {
                if (matched.Contains(x)) component++;
                else if (buffer[x].Class == GlyphClass.Mark)
                {
                    buffer[x].LigatureId = id;
                    buffer[x].LigatureComponent = component;
                }
            }
            for (int k = matched.Count - 1; k >= 1; k--) buffer.RemoveAt(matched[k]);
            Replace(first, ligGlyph);
            first.Cluster = cluster;
            first.LigatureId = id;
            first.ComponentCount = components;
            if (_gdef == null || !_gdef.HasGlyphClasses) first.Class = GlyphClass.Ligature;
            // marks after the last component belong to the last component
            for (int x = pos + 1; x < buffer.Count && buffer[x].Class == GlyphClass.Mark && buffer[x].LigatureId == 0; x++)
            {
                buffer[x].LigatureId = id;
                buffer[x].LigatureComponent = components;
            }
            return pos + 1;
        }
        return -1;
    }

    // ------------------------------------------------------------------ contexts (GSUB 5/6, GPOS 7/8)

    private int Context(List<ShapingGlyph> buffer, int pos, Lookup lookup, int sub, bool chained, int depth)
    {
        int format = U16(sub);
        int gid = buffer[pos].Gid;
        var matched = new List<int>();
        int recordsAt = -1, recordCount = 0;

        bool MatchSequence(int start, int count, Func<int, int, bool> matches, List<int>? into)
        {
            // input sequence after the first glyph
            int j = start;
            for (int k = 0; k < count; k++)
            {
                j = Next(buffer, j, lookup);
                if (j < 0 || !matches(k, buffer[j].Gid)) return false;
                into?.Add(j);
            }
            return true;
        }

        bool MatchBackward(int start, int count, Func<int, int, bool> matches)
        {
            int j = start;
            for (int k = 0; k < count; k++)
            {
                j = Previous(buffer, j, lookup);
                if (j < 0 || !matches(k, buffer[j].Gid)) return false;
            }
            return true;
        }

        switch (format)
        {
            case 1:
            {
                int ci = Coverage(sub + U16(sub + 2), gid);
                if (ci < 0 || ci >= U16(sub + 4)) return -1;
                int set = U16(sub + 6 + 2 * ci);
                if (set == 0) return -1;
                set += sub;
                int ruleCount = U16(set);
                for (int r = 0; r < ruleCount; r++)
                {
                    int rule = set + U16(set + 2 + 2 * r);
                    matched.Clear();
                    matched.Add(pos);
                    if (!chained)
                    {
                        int inputCount = U16(rule), substCount = U16(rule + 2);
                        if (inputCount == 0 || !MatchSequence(pos, inputCount - 1, (k, g) => g == U16(rule + 4 + 2 * k), matched)) continue;
                        recordsAt = rule + 4 + 2 * (inputCount - 1);
                        recordCount = substCount;
                    }
                    else
                    {
                        int back = U16(rule);
                        int input = rule + 2 + 2 * back, inputCount = U16(input);
                        int ahead = input + 2 + 2 * Math.Max(0, inputCount - 1), aheadCount = U16(ahead);
                        if (inputCount == 0 || !MatchBackward(pos, back, (k, g) => g == U16(rule + 2 + 2 * k))) continue;
                        if (!MatchSequence(pos, inputCount - 1, (k, g) => g == U16(input + 2 + 2 * k), matched)) continue;
                        if (!MatchSequence(matched[^1], aheadCount, (k, g) => g == U16(ahead + 2 + 2 * k), null)) continue;
                        recordsAt = ahead + 2 + 2 * aheadCount + 2;
                        recordCount = U16(ahead + 2 + 2 * aheadCount);
                    }
                    return ApplyRecords(buffer, matched, recordsAt, recordCount, depth);
                }
                return -1;
            }
            case 2:
            {
                if (Coverage(sub + U16(sub + 2), gid) < 0) return -1;
                int backClass = chained ? sub + U16(sub + 4) : 0;
                int inputClass = chained ? sub + U16(sub + 6) : sub + U16(sub + 4);
                int aheadClass = chained ? sub + U16(sub + 8) : 0;
                int setCountAt = chained ? sub + 10 : sub + 6;
                int cls = ClassOf(inputClass, gid);
                if (cls >= U16(setCountAt)) return -1;
                int set = U16(setCountAt + 2 + 2 * cls);
                if (set == 0) return -1;
                set += sub;
                int ruleCount = U16(set);
                for (int r = 0; r < ruleCount; r++)
                {
                    int rule = set + U16(set + 2 + 2 * r);
                    matched.Clear();
                    matched.Add(pos);
                    if (!chained)
                    {
                        int inputCount = U16(rule), substCount = U16(rule + 2);
                        if (inputCount == 0 || !MatchSequence(pos, inputCount - 1, (k, g) => ClassOf(inputClass, g) == U16(rule + 4 + 2 * k), matched)) continue;
                        recordsAt = rule + 4 + 2 * (inputCount - 1);
                        recordCount = substCount;
                    }
                    else
                    {
                        int back = U16(rule);
                        int input = rule + 2 + 2 * back, inputCount = U16(input);
                        int ahead = input + 2 + 2 * Math.Max(0, inputCount - 1), aheadCount = U16(ahead);
                        if (inputCount == 0 || !MatchBackward(pos, back, (k, g) => ClassOf(backClass, g) == U16(rule + 2 + 2 * k))) continue;
                        if (!MatchSequence(pos, inputCount - 1, (k, g) => ClassOf(inputClass, g) == U16(input + 2 + 2 * k), matched)) continue;
                        if (!MatchSequence(matched[^1], aheadCount, (k, g) => ClassOf(aheadClass, g) == U16(ahead + 2 + 2 * k), null)) continue;
                        recordsAt = ahead + 2 + 2 * aheadCount + 2;
                        recordCount = U16(ahead + 2 + 2 * aheadCount);
                    }
                    return ApplyRecords(buffer, matched, recordsAt, recordCount, depth);
                }
                return -1;
            }
            case 3:
            {
                matched.Add(pos);
                if (!chained)
                {
                    int inputCount = U16(sub + 2), substCount = U16(sub + 4);
                    if (inputCount == 0 || Coverage(sub + U16(sub + 6), gid) < 0) return -1;
                    if (!MatchSequence(pos, inputCount - 1, (k, g) => Coverage(sub + U16(sub + 8 + 2 * k), g) >= 0, matched)) return -1;
                    return ApplyRecords(buffer, matched, sub + 6 + 2 * inputCount, substCount, depth);
                }
                int back = U16(sub + 2);
                int input = sub + 4 + 2 * back, inputCount2 = U16(input);
                int ahead = input + 2 + 2 * inputCount2, aheadCount = U16(ahead);
                if (inputCount2 == 0 || Coverage(sub + U16(input + 2), gid) < 0) return -1;
                if (!MatchBackward(pos, back, (k, g) => Coverage(sub + U16(sub + 4 + 2 * k), g) >= 0)) return -1;
                if (!MatchSequence(pos, inputCount2 - 1, (k, g) => Coverage(sub + U16(input + 4 + 2 * k), g) >= 0, matched)) return -1;
                if (!MatchSequence(matched[^1], aheadCount, (k, g) => Coverage(sub + U16(ahead + 2 + 2 * k), g) >= 0, null)) return -1;
                int records = ahead + 2 + 2 * aheadCount;
                return ApplyRecords(buffer, matched, records + 2, U16(records), depth);
            }
        }
        return -1;
    }

    /// <summary>Applies the nested lookups of a matched context (sequence index, lookup index records).</summary>
    private int ApplyRecords(List<ShapingGlyph> buffer, List<int> matched, int records, int count, int depth)
    {
        int end = matched[^1] + 1;
        for (int r = 0; r < count; r++)
        {
            int sequenceIndex = U16(records + 4 * r), lookupIndex = U16(records + 4 * r + 2);
            if (sequenceIndex >= matched.Count) continue;
            int at = matched[sequenceIndex];
            if (at >= buffer.Count) continue;
            var nested = GetLookup(lookupIndex);
            int before = buffer.Count;
            foreach (int sub in nested.Subtables)
                if (ApplySubtable(buffer, at, nested, sub, depth + 1) >= 0) break;
            int delta = buffer.Count - before;
            if (delta != 0)
            {
                for (int k = 0; k < matched.Count; k++)
                    if (matched[k] > at) matched[k] += delta;
                end += delta;
            }
        }
        return Math.Max(end, matched[0] + 1);
    }

    private bool ReverseChain(List<ShapingGlyph> buffer, int pos, int sub, Lookup lookup)
    {
        if (U16(sub) != 1) return false;
        int ci = Coverage(sub + U16(sub + 2), buffer[pos].Gid);
        if (ci < 0) return false;
        int back = U16(sub + 4);
        int ahead = sub + 6 + 2 * back, aheadCount = U16(ahead);
        int subst = ahead + 2 + 2 * aheadCount;
        int j = pos;
        for (int k = 0; k < back; k++)
        {
            j = Previous(buffer, j, lookup);
            if (j < 0 || Coverage(sub + U16(sub + 6 + 2 * k), buffer[j].Gid) < 0) return false;
        }
        j = pos;
        for (int k = 0; k < aheadCount; k++)
        {
            j = Next(buffer, j, lookup);
            if (j < 0 || Coverage(sub + U16(ahead + 2 + 2 * k), buffer[j].Gid) < 0) return false;
        }
        if (ci >= U16(subst)) return false;
        Replace(buffer[pos], U16(subst + 2 + 2 * ci));
        return true;
    }

    // ------------------------------------------------------------------ GPOS

    private static int ValueSize(int format) => 2 * System.Numerics.BitOperations.PopCount((uint)format & 0xFF);

    private void ApplyValue(ShapingGlyph g, int sub, int record, int format)
    {
        int p = record;
        if ((format & 1) != 0) { g.XOffset += S16(p); p += 2; }
        if ((format & 2) != 0) { g.YOffset += S16(p); p += 2; }
        if ((format & 4) != 0) { g.XAdvance += S16(p); p += 2; }
        if ((format & 8) != 0) { g.YAdvance += S16(p); }
    }

    private int Pair(List<ShapingGlyph> buffer, int pos, Lookup lookup, int sub)
    {
        int format = U16(sub);
        int ci = Coverage(sub + U16(sub + 2), buffer[pos].Gid);
        if (ci < 0) return -1;
        int second = Next(buffer, pos, lookup);
        if (second < 0) return -1;
        int vf1 = U16(sub + 4), vf2 = U16(sub + 6);
        int size1 = ValueSize(vf1), size2 = ValueSize(vf2);
        if (format == 1)
        {
            if (ci >= U16(sub + 8)) return -1;
            int set = sub + U16(sub + 10 + 2 * ci);
            int count = U16(set), recordSize = 2 + size1 + size2;
            int lo = 0, hi = count - 1, target = buffer[second].Gid;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1, rec = set + 2 + mid * recordSize, g2 = U16(rec);
                if (g2 == target)
                {
                    ApplyValue(buffer[pos], sub, rec + 2, vf1);
                    ApplyValue(buffer[second], sub, rec + 2 + size1, vf2);
                    return vf2 != 0 ? second + 1 : second;
                }
                if (g2 < target) lo = mid + 1;
                else hi = mid - 1;
            }
            return -1;
        }
        if (format == 2)
        {
            int c1 = ClassOf(sub + U16(sub + 8), buffer[pos].Gid), c2 = ClassOf(sub + U16(sub + 10), buffer[second].Gid);
            int count1 = U16(sub + 12), count2 = U16(sub + 14);
            if (c1 >= count1 || c2 >= count2) return -1;
            int rec = sub + 16 + (c1 * count2 + c2) * (size1 + size2);
            ApplyValue(buffer[pos], sub, rec, vf1);
            ApplyValue(buffer[second], sub, rec + size1, vf2);
            return vf2 != 0 ? second + 1 : second;
        }
        return -1;
    }

    private (int X, int Y)? Anchor(int offset) => offset <= 0 || offset >= _d.Length ? null : (S16(offset + 2), S16(offset + 4));

    private int Cursive(List<ShapingGlyph> buffer, int pos, Lookup lookup, int sub)
    {
        int coverage = sub + U16(sub + 2);
        int ci = Coverage(coverage, buffer[pos].Gid);
        if (ci < 0) return -1;
        int next = Next(buffer, pos, lookup);
        if (next < 0) return -1;
        int cj = Coverage(coverage, buffer[next].Gid);
        if (cj < 0) return -1;
        int exitOffset = U16(sub + 6 + 4 * ci + 2), entryOffset = U16(sub + 6 + 4 * cj);
        if (exitOffset == 0 || entryOffset == 0) return -1;
        var exit = Anchor(sub + exitOffset)!.Value;
        var entry = Anchor(sub + entryOffset)!.Value;
        var a = buffer[pos];
        var b = buffer[next];
        if ((lookup.Flag & RightToLeft) == 0)
        {
            // left to right: the first glyph ends at its exit, the second starts at its entry
            a.XAdvance = exit.X + a.XOffset;
            int d = entry.X + b.XOffset;
            b.XAdvance -= d;
            b.XOffset -= d;
        }
        else
        {
            int d = exit.X + a.XOffset;
            a.XAdvance -= d;
            a.XOffset -= d;
            b.XAdvance = entry.X + b.XOffset;
        }
        // vertical: the following glyph (logical order) hangs on the previous one
        b.YOffset = a.YOffset + exit.Y - entry.Y;
        b.CursiveAttached = true;
        return next;
    }

    private int MarkAttachment(List<ShapingGlyph> buffer, int pos, Lookup lookup, int sub, int type)
    {
        var mark = buffer[pos];
        int markIndex = Coverage(sub + U16(sub + 2), mark.Gid);
        if (markIndex < 0) return -1;
        // the glyph to attach to: the preceding base (or ligature), or the preceding mark for mark-to-mark
        int target = -1;
        for (int j = pos - 1; j >= 0; j--)
        {
            var g = buffer[j];
            if (type == 6)
            {
                if (g.Class == GlyphClass.Mark && !Ignored(g, lookup)) target = j;
                break;
            }
            if (g.Class == GlyphClass.Mark) continue;
            target = j;
            break;
        }
        if (target < 0) return -1;
        var baseGlyph = buffer[target];
        int baseIndex = Coverage(sub + U16(sub + 4), baseGlyph.Gid);
        if (baseIndex < 0) return -1;
        int classCount = U16(sub + 6);
        int markArray = sub + U16(sub + 8), baseArray = sub + U16(sub + 10);
        if (markIndex >= U16(markArray)) return -1;
        int markClass = U16(markArray + 2 + 4 * markIndex);
        var markAnchor = Anchor(markArray + U16(markArray + 2 + 4 * markIndex + 2));
        if (markClass >= classCount || markAnchor == null) return -1;
        int anchorOffset;
        if (type == 5)
        {
            // mark to ligature: the anchor of the mark's component
            if (baseIndex >= U16(baseArray)) return -1;
            int attach = baseArray + U16(baseArray + 2 + 2 * baseIndex);
            int components = U16(attach);
            if (components == 0) return -1;
            int component = mark.LigatureId != 0 && mark.LigatureId == baseGlyph.LigatureId && mark.LigatureComponent > 0
                ? Math.Min(mark.LigatureComponent, components) - 1 : components - 1;
            anchorOffset = U16(attach + 2 + (component * classCount + markClass) * 2);
            if (anchorOffset == 0) return -1;
            anchorOffset += attach;
        }
        else
        {
            if (baseIndex >= U16(baseArray)) return -1;
            anchorOffset = U16(baseArray + 2 + (baseIndex * classCount + markClass) * 2);
            if (anchorOffset == 0) return -1;
            anchorOffset += baseArray;
        }
        var baseAnchor = Anchor(anchorOffset);
        if (baseAnchor == null) return -1;
        mark.AttachTo = target;
        mark.XOffset = baseAnchor.Value.X - markAnchor.Value.X;
        mark.YOffset = baseAnchor.Value.Y - markAnchor.Value.Y;
        mark.XAdvance = 0;
        mark.YAdvance = 0;
        return pos + 1;
    }

    // ------------------------------------------------------------------ coverage and classes

    private int Coverage(int table, int gid)
    {
        int format = U16(table);
        if (format == 1)
        {
            int lo = 0, hi = U16(table + 2) - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1, g = U16(table + 4 + 2 * mid);
                if (g == gid) return mid;
                if (g < gid) lo = mid + 1;
                else hi = mid - 1;
            }
            return -1;
        }
        if (format == 2)
        {
            int lo = 0, hi = U16(table + 2) - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1, rec = table + 4 + 6 * mid;
                if (gid < U16(rec)) hi = mid - 1;
                else if (gid > U16(rec + 2)) lo = mid + 1;
                else return U16(rec + 4) + gid - U16(rec);
            }
        }
        return -1;
    }

    private int ClassOf(int table, int gid) => Gdef.ClassDef(_d, table, gid);

    /// <summary>The GDEF table: glyph classes, mark attachment classes and mark glyph sets.</summary>
    internal sealed class Gdef
    {
        private readonly byte[] _d;
        private readonly int _glyphClassDef, _markAttachClassDef, _markGlyphSets;

        public Gdef(byte[] data)
        {
            _d = data;
            _glyphClassDef = U16(data, 4);
            _markAttachClassDef = U16(data, 10);
            _markGlyphSets = U16(data, 0) == 1 && U16(data, 2) >= 2 ? U16(data, 12) : 0; // GDEF 1.2
        }

        private static int U16(byte[] d, int pos) => pos >= 0 && pos + 2 <= d.Length ? BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos)) : 0;

        public bool HasGlyphClasses => _glyphClassDef != 0;

        public GlyphClass ClassOf(int gid) => _glyphClassDef == 0 ? GlyphClass.Unknown : (GlyphClass)ClassDef(_d, _glyphClassDef, gid);

        public int MarkAttachClass(int gid) => _markAttachClassDef == 0 ? 0 : ClassDef(_d, _markAttachClassDef, gid);

        public bool InMarkSet(int set, int gid)
        {
            if (_markGlyphSets == 0 || set >= U16(_d, _markGlyphSets + 2)) return false;
            int coverage = _markGlyphSets + (int)BinaryPrimitives.ReadUInt32BigEndian(_d.AsSpan(_markGlyphSets + 4 + 4 * set));
            int format = U16(_d, coverage);
            if (format == 1)
            {
                int count = U16(_d, coverage + 2);
                for (int i = 0; i < count; i++)
                    if (U16(_d, coverage + 4 + 2 * i) == gid) return true;
            }
            else if (format == 2)
            {
                int count = U16(_d, coverage + 2);
                for (int i = 0; i < count; i++)
                {
                    int rec = coverage + 4 + 6 * i;
                    if (gid >= U16(_d, rec) && gid <= U16(_d, rec + 2)) return true;
                }
            }
            return false;
        }

        public static int ClassDef(byte[] d, int table, int gid)
        {
            if (table <= 0 || table + 4 > d.Length) return 0;
            int format = U16(d, table);
            if (format == 1)
            {
                int start = U16(d, table + 2), count = U16(d, table + 4);
                return gid >= start && gid < start + count ? U16(d, table + 6 + 2 * (gid - start)) : 0;
            }
            if (format == 2)
            {
                int lo = 0, hi = U16(d, table + 2) - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1, rec = table + 4 + 6 * mid;
                    if (gid < U16(d, rec)) hi = mid - 1;
                    else if (gid > U16(d, rec + 2)) lo = mid + 1;
                    else return U16(d, rec + 4);
                }
            }
            return 0;
        }
    }
}

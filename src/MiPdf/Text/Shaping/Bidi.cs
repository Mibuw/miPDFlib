namespace MiPdf.Text.Shaping;

/// <summary>
/// The Unicode Bidirectional Algorithm (UAX #9) for one paragraph shown as one line: embedding levels (explicit
/// embeddings, overrides and isolates, weak and neutral types, paired brackets) and the visual order.
/// </summary>
internal static class Bidi
{
    private const int MaxDepth = 125;

    /// <summary>The resolved embedding level of every code point (even = left to right, odd = right to left).</summary>
    public static byte[] Levels(ReadOnlySpan<int> text, int? paragraphLevel = null)
    {
        int n = text.Length;
        var original = new BidiClass[n];
        for (int i = 0; i < n; i++) original[i] = UnicodeTables.BidiClassOf(text[i]);
        int baseLevel = paragraphLevel ?? FirstStrong(original, 0, n);
        var levels = new byte[n];
        var types = (BidiClass[])original.Clone();
        Explicit(original, types, levels, baseLevel);

        // sos and eos come from the explicit levels, before the implicit levels of other sequences are resolved
        var explicitLevels = (byte[])levels.Clone();
        foreach (var sequence in IsolatingRunSequences(original, types, levels, baseLevel))
            ResolveSequence(text, original, types, levels, explicitLevels, sequence, baseLevel);

        // characters removed by X9 (embedding controls, BN) take the level of the preceding character, so that they
        // do not split reversed runs
        for (int i = 0; i < n; i++)
            if (IsRemovedByX9(original[i])) levels[i] = i > 0 ? levels[i - 1] : (byte)baseLevel;

        // L1: segment separators, paragraph separators and trailing whitespace go back to the paragraph level
        for (int i = 0; i < n; i++)
        {
            if (original[i] is BidiClass.S or BidiClass.B)
            {
                levels[i] = (byte)baseLevel;
                for (int j = i - 1; j >= 0 && IsWhitespaceLike(original[j]); j--) levels[j] = (byte)baseLevel;
            }
        }
        for (int j = n - 1; j >= 0 && IsWhitespaceLike(original[j]); j--) levels[j] = (byte)baseLevel;
        return levels;
    }

    /// <summary>The paragraph level of a text: 1 if its first strong character is right to left, otherwise 0.</summary>
    public static int ParagraphLevel(ReadOnlySpan<int> text)
    {
        var classes = new BidiClass[text.Length];
        for (int i = 0; i < text.Length; i++) classes[i] = UnicodeTables.BidiClassOf(text[i]);
        return FirstStrong(classes, 0, classes.Length);
    }

    /// <summary>The visual order (L2): indices of the logical positions from left to right.</summary>
    public static int[] VisualOrder(byte[] levels)
    {
        int n = levels.Length;
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        if (n == 0) return order;
        int highest = levels.Max(), lowestOdd = levels.Where(l => (l & 1) == 1).DefaultIfEmpty((byte)255).Min();
        for (int level = highest; level >= lowestOdd && level > 0; level--)
        {
            int i = 0;
            while (i < n)
            {
                if (levels[order[i]] < level)
                {
                    i++;
                    continue;
                }
                int start = i;
                while (i < n && levels[order[i]] >= level) i++;
                Array.Reverse(order, start, i - start);
            }
        }
        return order;
    }

    private static bool IsWhitespaceLike(BidiClass c) =>
        c is BidiClass.WS or BidiClass.FSI or BidiClass.LRI or BidiClass.RLI or BidiClass.PDI or BidiClass.BN
            or BidiClass.LRE or BidiClass.RLE or BidiClass.LRO or BidiClass.RLO or BidiClass.PDF;

    private static bool IsIsolateInitiator(BidiClass c) => c is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI;

    private static bool IsRemovedByX9(BidiClass c) =>
        c is BidiClass.LRE or BidiClass.RLE or BidiClass.LRO or BidiClass.RLO or BidiClass.PDF or BidiClass.BN;

    /// <summary>P2/P3: the level of the first strong character, skipping isolates.</summary>
    private static int FirstStrong(BidiClass[] classes, int start, int end)
    {
        int isolates = 0;
        for (int i = start; i < end; i++)
        {
            var c = classes[i];
            if (IsIsolateInitiator(c)) isolates++;
            else if (c == BidiClass.PDI)
            {
                if (isolates > 0) isolates--;
            }
            else if (isolates == 0)
            {
                if (c == BidiClass.L) return 0;
                if (c is BidiClass.R or BidiClass.AL) return 1;
                if (c == BidiClass.B) break;
            }
        }
        return 0;
    }

    private static int MatchingPdi(BidiClass[] classes, int initiator)
    {
        int depth = 1;
        for (int i = initiator + 1; i < classes.Length; i++)
        {
            if (IsIsolateInitiator(classes[i])) depth++;
            else if (classes[i] == BidiClass.PDI && --depth == 0) return i;
            else if (classes[i] == BidiClass.B) break;
        }
        return -1;
    }

    /// <summary>X1-X8: explicit embedding levels and overrides.</summary>
    private static void Explicit(BidiClass[] original, BidiClass[] types, byte[] levels, int baseLevel)
    {
        var stack = new Stack<(int Level, BidiClass? Override, bool Isolate)>();
        stack.Push((baseLevel, null, false));
        int overflowIsolates = 0, overflowEmbeddings = 0, validIsolates = 0;
        for (int i = 0; i < original.Length; i++)
        {
            var c = original[i];
            var top = stack.Peek();
            switch (c)
            {
                case BidiClass.RLE or BidiClass.LRE or BidiClass.RLO or BidiClass.LRO:
                {
                    bool rtl = c is BidiClass.RLE or BidiClass.RLO;
                    int level = rtl ? (top.Level + 1) | 1 : (top.Level + 2) & ~1;
                    levels[i] = (byte)top.Level;
                    if (level <= MaxDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                        stack.Push((level, c == BidiClass.RLO ? BidiClass.R : c == BidiClass.LRO ? BidiClass.L : null, false));
                    else if (overflowIsolates == 0) overflowEmbeddings++;
                    break;
                }
                case BidiClass.RLI or BidiClass.LRI or BidiClass.FSI:
                {
                    levels[i] = (byte)top.Level;
                    if (top.Override is { } o) types[i] = o;
                    bool rtl = c == BidiClass.RLI;
                    if (c == BidiClass.FSI)
                    {
                        int end = MatchingPdi(original, i);
                        rtl = FirstStrong(original, i + 1, end < 0 ? original.Length : end) == 1;
                    }
                    int level = rtl ? (top.Level + 1) | 1 : (top.Level + 2) & ~1;
                    if (level <= MaxDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                    {
                        validIsolates++;
                        stack.Push((level, null, true));
                    }
                    else overflowIsolates++;
                    break;
                }
                case BidiClass.PDI:
                    if (overflowIsolates > 0) overflowIsolates--;
                    else if (validIsolates > 0)
                    {
                        overflowEmbeddings = 0;
                        while (!stack.Peek().Isolate) stack.Pop();
                        stack.Pop();
                        validIsolates--;
                    }
                    top = stack.Peek();
                    levels[i] = (byte)top.Level;
                    if (top.Override is { } po) types[i] = po;
                    break;
                case BidiClass.PDF:
                    levels[i] = (byte)top.Level;
                    if (overflowIsolates > 0) { }
                    else if (overflowEmbeddings > 0) overflowEmbeddings--;
                    else if (!top.Isolate && stack.Count >= 2) stack.Pop();
                    break;
                case BidiClass.B:
                    levels[i] = (byte)baseLevel;
                    break;
                case BidiClass.BN:
                    levels[i] = (byte)top.Level;
                    break;
                default:
                    levels[i] = (byte)top.Level;
                    if (top.Override is { } ov) types[i] = ov;
                    break;
            }
        }
    }

    /// <summary>X10 / BD13: the isolating run sequences (lists of indices, characters removed by X9 excluded).</summary>
    private static List<List<int>> IsolatingRunSequences(BidiClass[] original, BidiClass[] types, byte[] levels, int baseLevel)
    {
        int n = original.Length;
        // level runs over the characters not removed by X9
        var runs = new List<List<int>>();
        List<int>? current = null;
        int currentLevel = -1;
        for (int i = 0; i < n; i++)
        {
            if (IsRemovedByX9(original[i])) continue;
            if (current == null || levels[i] != currentLevel)
            {
                current = new List<int>();
                runs.Add(current);
                currentLevel = levels[i];
            }
            current.Add(i);
        }
        var runOf = new Dictionary<int, int>();
        for (int r = 0; r < runs.Count; r++) runOf[runs[r][0]] = r;
        var sequences = new List<List<int>>();
        var used = new bool[runs.Count];
        for (int r = 0; r < runs.Count; r++)
        {
            if (used[r]) continue;
            // a run starting with the PDI of an isolate continues the sequence of its initiator
            if (original[runs[r][0]] == BidiClass.PDI && sequences.Count > 0 && IsContinuation(original, runs, r)) continue;
            var sequence = new List<int>();
            int run = r;
            while (run >= 0)
            {
                used[run] = true;
                sequence.AddRange(runs[run]);
                int last = runs[run][^1];
                run = -1;
                if (IsIsolateInitiator(original[last]))
                {
                    int pdi = MatchingPdi(original, last);
                    if (pdi >= 0 && runOf.TryGetValue(pdi, out int next)) run = next;
                }
            }
            sequences.Add(sequence);
        }
        return sequences;
    }

    private static bool IsContinuation(BidiClass[] original, List<List<int>> runs, int r)
    {
        int pdi = runs[r][0];
        for (int k = 0; k < r; k++)
        {
            int last = runs[k][^1];
            if (IsIsolateInitiator(original[last]) && MatchingPdi(original, last) == pdi) return true;
        }
        return false;
    }

    /// <summary>W1-W7, N0-N2, I1-I2 for one isolating run sequence.</summary>
    private static void ResolveSequence(ReadOnlySpan<int> text, BidiClass[] original, BidiClass[] types, byte[] levels, byte[] explicitLevels, List<int> seq, int baseLevel)
    {
        int count = seq.Count;
        if (count == 0) return;
        int level = explicitLevels[seq[0]];
        // sos and eos from the levels of the neighboring characters (not removed by X9)
        int before = PreviousLevel(original, explicitLevels, seq[0], baseLevel);
        int lastIndex = seq[^1];
        int after = IsIsolateInitiator(original[lastIndex]) ? baseLevel : NextLevel(original, explicitLevels, lastIndex, baseLevel);
        var sos = (Math.Max(before, level) & 1) == 1 ? BidiClass.R : BidiClass.L;
        var eos = (Math.Max(after, explicitLevels[lastIndex]) & 1) == 1 ? BidiClass.R : BidiClass.L;
        var t = new BidiClass[count];
        for (int k = 0; k < count; k++) t[k] = types[seq[k]];

        // W1: NSM takes the type of the previous character (ON after isolate initiators and PDI)
        for (int k = 0; k < count; k++)
            if (t[k] == BidiClass.NSM)
                t[k] = k == 0 ? sos : IsIsolateInitiator(t[k - 1]) || t[k - 1] == BidiClass.PDI ? BidiClass.ON : t[k - 1];
        // W2: EN after AL becomes AN; W3: AL becomes R
        var lastStrong = sos;
        for (int k = 0; k < count; k++)
        {
            if (t[k] is BidiClass.L or BidiClass.R or BidiClass.AL) lastStrong = t[k];
            else if (t[k] == BidiClass.EN && lastStrong == BidiClass.AL) t[k] = BidiClass.AN;
        }
        for (int k = 0; k < count; k++)
            if (t[k] == BidiClass.AL) t[k] = BidiClass.R;
        // W4: a single separator between two numbers of the same kind
        for (int k = 1; k < count - 1; k++)
        {
            if (t[k] == BidiClass.ES && t[k - 1] == BidiClass.EN && t[k + 1] == BidiClass.EN) t[k] = BidiClass.EN;
            else if (t[k] == BidiClass.CS && t[k - 1] == t[k + 1] && t[k - 1] is BidiClass.EN or BidiClass.AN) t[k] = t[k - 1];
        }
        // W5: terminators next to European numbers
        for (int k = 0; k < count; k++)
        {
            if (t[k] != BidiClass.ET) continue;
            int end = k;
            while (end < count && t[end] == BidiClass.ET) end++;
            bool en = (k > 0 && t[k - 1] == BidiClass.EN) || (end < count && t[end] == BidiClass.EN);
            if (en)
                for (int m = k; m < end; m++) t[m] = BidiClass.EN;
            k = end - 1;
        }
        // W6: remaining separators and terminators become ON
        for (int k = 0; k < count; k++)
            if (t[k] is BidiClass.ES or BidiClass.ET or BidiClass.CS) t[k] = BidiClass.ON;
        // W7: EN after L (or sos L) becomes L
        lastStrong = sos;
        for (int k = 0; k < count; k++)
        {
            if (t[k] is BidiClass.L or BidiClass.R) lastStrong = t[k];
            else if (t[k] == BidiClass.EN && lastStrong == BidiClass.L) t[k] = BidiClass.L;
        }

        PairedBrackets(text, original, seq, t, level, sos);

        // N1/N2: neutrals (and isolate controls) between strong types
        bool IsNeutral(BidiClass c) => c is BidiClass.B or BidiClass.S or BidiClass.WS or BidiClass.ON or BidiClass.FSI
            or BidiClass.LRI or BidiClass.RLI or BidiClass.PDI;
        BidiClass Strong(BidiClass c) => c is BidiClass.EN or BidiClass.AN ? BidiClass.R : c;
        var embedding = (level & 1) == 1 ? BidiClass.R : BidiClass.L;
        for (int k = 0; k < count; k++)
        {
            if (!IsNeutral(t[k])) continue;
            int end = k;
            while (end < count && IsNeutral(t[end])) end++;
            var prev = k == 0 ? sos : Strong(t[k - 1]);
            var next = end == count ? eos : Strong(t[end]);
            var resolved = prev == next ? prev : embedding;
            for (int m = k; m < end; m++) t[m] = resolved;
            k = end - 1;
        }
        // I1/I2: implicit levels
        for (int k = 0; k < count; k++)
        {
            int i = seq[k];
            int l = levels[i];
            if ((l & 1) == 0)
            {
                if (t[k] == BidiClass.R) l += 1;
                else if (t[k] is BidiClass.AN or BidiClass.EN) l += 2;
            }
            else if (t[k] is BidiClass.L or BidiClass.EN or BidiClass.AN) l += 1;
            levels[i] = (byte)Math.Min(l, MaxDepth + 1);
            types[i] = t[k];
        }
    }

    /// <summary>N0: paired brackets take the direction of the text they enclose (BD16).</summary>
    private static void PairedBrackets(ReadOnlySpan<int> text, BidiClass[] original, List<int> seq, BidiClass[] t, int level, BidiClass sos)
    {
        var openers = new List<(int Pair, int Position)>();
        var pairs = new List<(int Open, int Close)>();
        for (int k = 0; k < seq.Count; k++)
        {
            if (t[k] != BidiClass.ON) continue;
            int cp = text[seq[k]];
            if (UnicodeTables.Bracket(cp) is not { } bracket) continue;
            if (bracket.Opening)
            {
                if (openers.Count >= 63) break;
                openers.Add((bracket.Pair, k));
            }
            else
            {
                for (int o = openers.Count - 1; o >= 0; o--)
                {
                    // U+2329/U+232A and U+3008/U+3009 are canonically equivalent
                    int pair = openers[o].Pair;
                    if (pair == cp || (pair == 0x232A && cp == 0x3009) || (pair == 0x3009 && cp == 0x232A))
                    {
                        pairs.Add((openers[o].Position, k));
                        openers.RemoveRange(o, openers.Count - o);
                        break;
                    }
                }
            }
        }
        pairs.Sort((a, b) => a.Open.CompareTo(b.Open));
        var embedding = (level & 1) == 1 ? BidiClass.R : BidiClass.L;
        BidiClass StrongOf(BidiClass c) => c is BidiClass.L ? BidiClass.L : c is BidiClass.R or BidiClass.AL or BidiClass.EN or BidiClass.AN ? BidiClass.R : BidiClass.ON;
        foreach (var (open, close) in pairs)
        {
            bool foundEmbedding = false, foundOpposite = false;
            for (int k = open + 1; k < close; k++)
            {
                var s = StrongOf(t[k]);
                if (s == BidiClass.ON) continue;
                if (s == embedding) foundEmbedding = true;
                else foundOpposite = true;
            }
            BidiClass? direction = null;
            if (foundEmbedding) direction = embedding;
            else if (foundOpposite)
            {
                // the direction of the context before the opening bracket decides
                var context = sos;
                for (int k = open - 1; k >= 0; k--)
                {
                    var s = StrongOf(t[k]);
                    if (s != BidiClass.ON)
                    {
                        context = s;
                        break;
                    }
                }
                direction = context != embedding ? context : embedding;
            }
            if (direction is not { } d) continue;
            t[open] = t[close] = d;
            // NSMs after the brackets take their type
            for (int k = open + 1; k < seq.Count && original[seq[k]] == BidiClass.NSM; k++) t[k] = d;
            for (int k = close + 1; k < seq.Count && original[seq[k]] == BidiClass.NSM; k++) t[k] = d;
        }
    }

    private static int PreviousLevel(BidiClass[] original, byte[] levels, int index, int baseLevel)
    {
        for (int i = index - 1; i >= 0; i--)
            if (!IsRemovedByX9(original[i])) return levels[i];
        return baseLevel;
    }

    private static int NextLevel(BidiClass[] original, byte[] levels, int index, int baseLevel)
    {
        for (int i = index + 1; i < original.Length; i++)
            if (!IsRemovedByX9(original[i])) return levels[i];
        return baseLevel;
    }
}

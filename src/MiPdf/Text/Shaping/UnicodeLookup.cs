using System.Globalization;

namespace MiPdf.Text.Shaping;

/// <summary>Arabic joining types (Unicode, ArabicShaping.txt).</summary>
internal enum JoiningType : byte
{
    /// <summary>Right joining: connects to the preceding (right) letter only.</summary>
    Right,

    /// <summary>Left joining (rare).</summary>
    Left,

    /// <summary>Dual joining: connects on both sides.</summary>
    Dual,

    /// <summary>Join causing (tatweel, ZWJ).</summary>
    Causing,

    /// <summary>Transparent: marks, skipped when joining.</summary>
    Transparent,

    /// <summary>Non joining.</summary>
    None,
}

/// <summary>Lookups in the generated Unicode tables.</summary>
internal static partial class UnicodeTables
{
    private static int Find(int[] starts, int cp)
    {
        int lo = 0, hi = starts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if ((starts[mid] >> 8) <= cp) lo = mid;
            else hi = mid - 1;
        }
        return starts[lo] & 0xFF;
    }

    public static BidiClass BidiClassOf(int cp) => cp is < 0 or > 0x10FFFF ? BidiClass.L : (BidiClass)Find(BidiStarts, cp);

    public static UnicodeScript ScriptOf(int cp) => cp is < 0 or > 0x10FFFF ? UnicodeScript.Common : (UnicodeScript)Find(ScriptStarts, cp);

    public static JoiningType JoiningTypeOf(int cp)
    {
        int lo = 0, hi = JoiningEntries.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1, value = JoiningEntries[mid] >> 8;
            if (value == cp) return (JoiningType)(JoiningEntries[mid] & 0xFF);
            if (value < cp) lo = mid + 1;
            else hi = mid - 1;
        }
        // not listed: marks and format characters are transparent, everything else does not join
        var category = cp <= 0x10FFFF ? CharUnicodeInfo.GetUnicodeCategory(cp) : UnicodeCategory.OtherNotAssigned;
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format
            ? JoiningType.Transparent
            : JoiningType.None;
    }

    /// <summary>The mirrored character (e.g. ')' for '('), or the character itself.</summary>
    public static int Mirror(int cp)
    {
        int lo = 0, hi = MirrorPairs.Length / 2 - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1, value = MirrorPairs[2 * mid];
            if (value == cp) return MirrorPairs[2 * mid + 1];
            if (value < cp) lo = mid + 1;
            else hi = mid - 1;
        }
        return cp;
    }

    /// <summary>The paired bracket of a bracket (BidiBrackets.txt), or null.</summary>
    public static (int Pair, bool Opening)? Bracket(int cp)
    {
        int lo = 0, hi = BracketPairs.Length / 3 - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1, value = BracketPairs[3 * mid];
            if (value == cp) return (BracketPairs[3 * mid + 1], BracketPairs[3 * mid + 2] == 1);
            if (value < cp) lo = mid + 1;
            else hi = mid - 1;
        }
        return null;
    }

    /// <summary>The Indic shaping category and position of a character (X and End for other characters).</summary>
    public static (IndicCategory Category, IndicPosition Position) IndicInfo(int cp)
    {
        int lo = 0, hi = IndicEntries.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1, value = (int)((uint)IndicEntries[mid] >> 12);
            if (value == cp) return ((IndicCategory)((IndicEntries[mid] >> 4) & 0xFF), (IndicPosition)(IndicEntries[mid] & 0xF));
            if (value < cp) lo = mid + 1;
            else hi = mid - 1;
        }
        return (IndicCategory.X, IndicPosition.End);
    }

    /// <summary>The canonical decomposition of a character in the Indic blocks, or null.</summary>
    public static (int First, int Second)? IndicDecomposition(int cp)
    {
        int lo = 0, hi = IndicDecompositions.Length / 3 - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1, value = IndicDecompositions[3 * mid];
            if (value == cp) return (IndicDecompositions[3 * mid + 1], IndicDecompositions[3 * mid + 2]);
            if (value < cp) lo = mid + 1;
            else hi = mid - 1;
        }
        return null;
    }

    /// <summary>The composition of a letter and a following mark (Indic blocks), or 0.</summary>
    public static int IndicComposition(int first, int second)
    {
        for (int i = 0; i < IndicCompositions.Length; i += 3)
            if (IndicCompositions[i] == first && IndicCompositions[i + 1] == second) return IndicCompositions[i + 2];
        return 0;
    }

    /// <summary>The canonical combining class of a character shaped as Indic (0 for others).</summary>
    public static int IndicCombiningClass(int cp)
    {
        int lo = 0, hi = IndicCombiningClasses.Length / 2 - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1, value = IndicCombiningClasses[2 * mid];
            if (value == cp) return IndicCombiningClasses[2 * mid + 1];
            if (value < cp) lo = mid + 1;
            else hi = mid - 1;
        }
        return 0;
    }

    /// <summary>True for nonspacing and enclosing marks (general category Mn, Me).</summary>
    public static bool IsMark(int cp) =>
        cp <= 0x10FFFF && CharUnicodeInfo.GetUnicodeCategory(cp) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark;
}

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

    /// <summary>True for nonspacing and enclosing marks (general category Mn, Me).</summary>
    public static bool IsMark(int cp) =>
        cp <= 0x10FFFF && CharUnicodeInfo.GetUnicodeCategory(cp) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark;
}

using MiPdf.Text.Shaping;

namespace MiPdf.Tests.Text;

/// <summary>The Unicode Bidirectional Algorithm (UAX #9).</summary>
public class BidiTests
{
    private static int[] Cps(string s)
    {
        var list = new List<int>();
        foreach (var rune in s.EnumerateRunes()) list.Add(rune.Value);
        return list.ToArray();
    }

    private static string Visual(string logical)
    {
        var cps = Cps(logical);
        var order = Bidi.VisualOrder(Bidi.Levels(cps));
        return string.Concat(order.Select(i => char.ConvertFromUtf32(cps[i])));
    }

    [Fact]
    public void ReordersMixedText()
    {
        // Hebrew letters (here written as their code points) inside Latin text are reversed, numbers stay in order
        const string alef = "א", bet = "ב", gimel = "ג";
        Assert.Equal("abc " + gimel + bet + alef + " def", Visual("abc " + alef + bet + gimel + " def"));
        Assert.Equal("123 " + bet + alef, Visual(alef + bet + " 123"));                 // RTL paragraph: number at the left
        Assert.Equal(1, Bidi.ParagraphLevel(Cps(alef + " abc")));
        Assert.Equal(0, Bidi.ParagraphLevel(Cps("1 abc " + alef)));
        // brackets keep their pairing in right-to-left text (they are mirrored when shaped)
        var levels = Bidi.Levels(Cps(alef + "(" + bet + ")"));
        Assert.All(levels, l => Assert.Equal(1, l));
    }

    /// <summary>
    /// The conformance test of the Unicode Character Database (BidiCharacterTest.txt, about 91,000 cases): runs when
    /// the environment variable MIPDF_BIDI_TEST names the file, as it is too large for the repository.
    /// </summary>
    [Fact]
    public void ConformanceTest()
    {
        var path = Environment.GetEnvironmentVariable("MIPDF_BIDI_TEST");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        int total = 0, failed = 0;
        var failures = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var f = line.Split(';');
            var cps = f[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => Convert.ToInt32(h, 16)).ToArray();
            int direction = int.Parse(f[1]);
            int? paragraph = direction == 2 ? null : direction;
            var expected = f[3].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var levels = Bidi.Levels(cps, paragraph);
            total++;
            bool ok = true;
            for (int i = 0; i < cps.Length && ok; i++)
                if (expected[i] != "x" && int.Parse(expected[i]) != levels[i]) ok = false;
            // the visual order of the characters not removed by rule X9
            var order = string.Join(' ', Bidi.VisualOrder(levels).Where(i => expected[i] != "x"));
            if (ok && order != f[4].Trim()) ok = false;
            if (!ok)
            {
                failed++;
                if (failures.Count < 10) failures.Add($"{line} => {string.Join(' ', levels)}");
            }
        }
        Assert.True(failed == 0, $"{failed} of {total} cases fail:\n" + string.Join('\n', failures));
    }
}

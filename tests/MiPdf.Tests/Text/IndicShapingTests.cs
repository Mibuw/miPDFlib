using MiPdf.Fonts;
using MiPdf.Fonts.TrueType;
using MiPdf.Layout;
using MiPdf.Text.Shaping;

namespace MiPdf.Tests.Text;

/// <summary>
/// Shaping of the Indic scripts: syllables, reordering of pre-base matras and reph, conjuncts and half forms, joiners,
/// dotted circles, and the output in PDF. The glyph output was compared with HarfBuzz on about 89,000 generated syllables
/// in Nirmala UI and the Noto Sans fonts of the nine scripts (identical glyphs and positions); these tests check the
/// behavior with whatever Indic font the system has.
/// </summary>
public class IndicShapingTests
{
    private static TrueTypeFont? Font() =>
        TestHelpers.FindIndicFont() is { } f ? new TrueTypeFont(File.ReadAllBytes(f.Path), f.Index) : null;

    private static bool Covers(TrueTypeFont font, string text) => text.EnumerateRunes().All(r => r.Value < 0x80 || font.GetGlyphId(r.Value) != 0);

    /// <summary>The glyphs in visual order.</summary>
    private static List<PositionedGlyph> Glyphs(TrueTypeFont font, string text) =>
        Shaper.ShapeLine(font, text, ligatures: false, kerning: false).Runs
            .SelectMany(r => r.Glyphs.Select(g => g with { X = g.X + r.Start })).ToList();

    [Fact]
    public void UnicodeData()
    {
        Assert.Equal((IndicCategory.C, IndicPosition.BaseC), UnicodeTables.IndicInfo(0x0915));     // KA
        Assert.Equal((IndicCategory.Ra, IndicPosition.BaseC), UnicodeTables.IndicInfo(0x0930));    // RA
        Assert.Equal((IndicCategory.M, IndicPosition.PreM), UnicodeTables.IndicInfo(0x093F));      // vowel sign I
        Assert.Equal((IndicCategory.M, IndicPosition.AfterSub), UnicodeTables.IndicInfo(0x0940));  // vowel sign II
        Assert.Equal(IndicCategory.H, UnicodeTables.IndicInfo(0x094D).Category);                   // virama
        Assert.Equal((IndicCategory.SM, IndicPosition.Smvd), UnicodeTables.IndicInfo(0x0902));     // anusvara
        Assert.Equal(IndicCategory.N, UnicodeTables.IndicInfo(0x093C).Category);                   // nukta
        Assert.Equal(IndicCategory.ZWJ, UnicodeTables.IndicInfo(0x200D).Category);
        Assert.Equal(IndicCategory.DottedCircle, UnicodeTables.IndicInfo(0x25CC).Category);
        Assert.Equal(IndicCategory.X, UnicodeTables.IndicInfo('A').Category);
        Assert.Equal((0x09C7, 0x09BE), UnicodeTables.IndicDecomposition(0x09CB));                   // Bengali O = E + AA
        Assert.Equal(0x0929, UnicodeTables.IndicComposition(0x0928, 0x093C));
        Assert.Equal(0, UnicodeTables.IndicComposition(0x0915, 0x093C));                           // QA is excluded
        Assert.Equal(7, UnicodeTables.IndicCombiningClass(0x093C));
        Assert.Equal(UnicodeScript.Devanagari, UnicodeTables.ScriptOf(0x0915));
        Assert.True(Shaper.NeedsShaping("कमल"));
        Assert.True(Shaper.NeedsShaping("தமிழ்"));
    }

    [Fact]
    public void PreBaseMatrasAreShownBeforeTheConsonant()
    {
        if (Font() is not { } font) return;
        foreach (var (consonant, matra) in new[] { ("क", "ि"), ("ক", "ি"), ("ક", "િ"), ("ਕ", "ਿ"), ("କ", "େ"), ("க", "ெ"), ("ക", "െ") })
        {
            if (!Covers(font, consonant + matra)) continue;
            var glyphs = Glyphs(font, consonant + matra);
            Assert.Equal(2, glyphs.Count);
            Assert.Equal(matra, glyphs[0].Text); // shown first, at the left
            Assert.Equal(consonant, glyphs[1].Text);
            Assert.True(glyphs[0].X < glyphs[1].X);
        }
    }

    [Fact]
    public void SplitVowelsSurroundTheConsonant()
    {
        if (Font() is not { } font) return;
        // Bengali O, Tamil O and Malayalam O decompose into a left and a right part
        foreach (var (consonant, vowel, left, right) in new[] { ("ক", "ো", "ে", "া"), ("க", "ொ", "ெ", "ா"), ("ക", "ൊ", "െ", "ാ") })
        {
            if (!Covers(font, consonant + vowel + left + right)) continue;
            var glyphs = Glyphs(font, consonant + vowel);
            Assert.Equal(new[] { left, consonant, right }, glyphs.Select(g => g.Text));
        }
    }

    [Fact]
    public void RephIsShownAfterTheConsonant()
    {
        if (Font() is not { } font || !Covers(font, "र्क")) return;
        var glyphs = Glyphs(font, "र्क");
        // Ra + virama form the reph, which is reordered after the consonant it stands above
        Assert.Equal(2, glyphs.Count);
        Assert.Equal("क", glyphs[0].Text);
        Assert.Equal("र्", glyphs[1].Text);
        Assert.NotEqual(font.GetGlyphId(0x0930), glyphs[1].Gid);
        // with a pre-base matra: matra, consonant, reph
        Assert.Equal(new[] { "ि", "क", "र्" }, Glyphs(font, "र्कि").Select(g => g.Text));
    }

    [Fact]
    public void ConjunctsHalfFormsAndVisibleVirama()
    {
        if (Font() is not { } font || !Covers(font, "कषसतह्")) return;
        // KSSA: one conjunct glyph
        var kssa = Assert.Single(Glyphs(font, "क्ष"));
        Assert.Equal("क्ष", kssa.Text);
        // SA + TA: a conjunct or the half form of SA followed by TA (depends on the font), never SA with a visible virama
        var sta = Glyphs(font, "स्त");
        Assert.DoesNotContain(sta, g => g.Gid == font.GetGlyphId('स') || g.Gid == font.GetGlyphId(0x094D));
        Assert.Equal("स्त", string.Concat(sta.Select(g => g.Text)));
        // a virama at the end of a word stays visible: no half form (which ZWJ requests explicitly)
        int halfKa = Glyphs(font, "क्‍ष")[0].Gid;
        var glyphs = Glyphs(font, "क्");
        Assert.DoesNotContain(glyphs, g => g.Gid == halfKa);
        Assert.Equal("क्", string.Concat(glyphs.Select(g => g.Text)));
    }

    [Fact]
    public void JoinersSelectTheForms()
    {
        if (Font() is not { } font || !Covers(font, "कष")) return;
        int ka = font.GetGlyphId('क'), virama = font.GetGlyphId(0x094D);
        // ZWNJ: no conjunct and no half form, the virama is shown; the joiner itself is not
        var nonJoined = Glyphs(font, "क्‌ष");
        Assert.Equal(3, nonJoined.Count);
        Assert.Equal(ka, nonJoined[0].Gid);
        Assert.Equal(virama, nonJoined[1].Gid);
        // ZWJ: the half form of KA instead of the conjunct
        var joined = Glyphs(font, "क्‍ष");
        Assert.Equal(2, joined.Count);
        Assert.NotEqual(ka, joined[0].Gid);
        Assert.Equal("ष", joined[1].Text);
        Assert.NotEqual(Glyphs(font, "क्ष").Single().Gid, joined[0].Gid);
    }

    [Fact]
    public void DottedCirclesForBrokenClustersAndInvalidVowelSequences()
    {
        if (Font() is not { } font || font.GetGlyphId(0x25CC) == 0 || !Covers(font, "िअा")) return;
        int dotted = font.GetGlyphId(0x25CC);
        // a vowel sign without consonant is shown on a dotted circle (that stands for no text)
        var broken = Glyphs(font, "ि");
        Assert.Equal(2, broken.Count);
        Assert.Equal("ि", broken[0].Text);
        Assert.Equal(dotted, broken[1].Gid);
        Assert.Equal("", broken[1].Text);
        // A + vowel sign AA looks like the vowel AA: a dotted circle is shown between them
        var invalid = Glyphs(font, "अा");
        Assert.Contains(invalid, g => g.Gid == dotted);
        Assert.Equal("अा", string.Concat(invalid.Select(g => g.Text)));
    }

    private static readonly string[] Samples =
    {
        "नमस्ते दुनिया, क्षत्रिय और श्रीमान् कार्यक्रम",
        "আমার সোনার বাংলা, কর্ম ও ক্ষমা",
        "ਪੰਜਾਬੀ ਭਾਸ਼ਾ ਅਤੇ ਗੁਰਮੁਖੀ ਲਿਪੀ",
        "ગુજરાતી ભાષા અને કર્મ",
        "ଓଡ଼ିଆ ଭାଷା ଓ କର୍ମ",
        "தமிழ் மொழி கொண்டு",
        "తెలుగు భాష మరియు కార్యక్రమం",
        "ಕನ್ನಡ ಭಾಷೆ ಮತ್ತು ಕಾರ್ಯಕ್ರಮ",
        "മലയാളം ഭാഷ, കൊണ്ട് കാര്യം",
    };

    [Fact]
    public void IndicTextIsWrittenAndExtractedInLogicalOrder()
    {
        if (TestHelpers.FindIndicFont() is not { } file || Font() is not { } ttf) return;
        var samples = Samples.Where(s => Covers(ttf, s)).ToList();
        var font = PdfTrueTypeFont.FromFile(file.Path, file.Index);
        var doc = PdfDocument.Create();
        var page = doc.AddPage(new PdfRectangle(0, 0, 500, 400));
        using (var canvas = page.GetCanvas())
            for (int i = 0; i < samples.Count; i++)
                canvas.BeginText().SetFont(font, 16).SetTextPosition(20, 370 - 36 * i).ShowText(samples[i]).EndText();
        // a paragraph wrapped by the layout engine
        string sentence = string.Join(" ", Enumerable.Repeat("भारत एक विशाल देश है जहाँ अनेक भाषाएँ बोली जाती हैं।", 3));
        bool devanagari = Covers(ttf, sentence + "क्ष");
        if (devanagari)
        {
            var layout = new LayoutDocument(doc, new PdfRectangle(0, 0, 300, 400));
            layout.Add(new Paragraph(sentence, font, 14));
            layout.Close();
        }
        using var reopened = PdfDocument.Open(doc.ToArray());
        Assert.Equal(samples, reopened.GetPage(1).ExtractText().Split('\n'));
        if (!devanagari) return;
        var wrapped = reopened.GetPage(2).ExtractText();
        Assert.Contains('\n', wrapped);
        Assert.Equal(sentence, wrapped.Replace("\n", " "));
        // the shaped width is measured (a conjunct is narrower than its parts)
        Assert.True(font.GetTextWidth("क्ष", 10) < font.GetTextWidth("क", 10) + font.GetTextWidth("्", 10) + font.GetTextWidth("ष", 10));
    }

    [Fact]
    public void TaggedIndicTextConformsToPdfUA()
    {
        if (TestHelpers.FindIndicFont() is not { } file) return;
        var font = PdfTrueTypeFont.FromFile(file.Path, file.Index);
        using var pdf = PdfDocument.Create();
        pdf.Info.Title = "हिन्दी";
        pdf.Language = "hi-IN";
        pdf.UAConformance = PdfUAConformance.PdfUA1;
        using (var layout = new LayoutDocument(pdf))
        {
            layout.Add(new Paragraph("हिन्दी भाषा", font, 20).AsHeading(1));
            layout.Add(new Paragraph("कार्यक्रम, क्षत्रिय और श्रीमान्।", font, 12));
        }
        Assert.Empty(pdf.CheckPdfUA());
        using var reopened = PdfDocument.Open(pdf.ToArray());
        Assert.Equal("हिन्दी भाषा\nकार्यक्रम, क्षत्रिय और श्रीमान्।", reopened.GetPage(1).ExtractText());
    }
}

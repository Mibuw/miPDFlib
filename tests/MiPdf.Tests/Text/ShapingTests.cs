using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Fonts.TrueType;
using MiPdf.Layout;
using MiPdf.Text.Shaping;

namespace MiPdf.Tests.Text;

/// <summary>Text shaping: Unicode data, Arabic joining, ligatures, marks, bidi runs and their output in PDF.</summary>
public class ShapingTests
{
    private const string Beh = "ب", Yeh = "ي", Teh = "ت", Lam = "ل", Alef = "ا", Fatha = "َ";
    private const string Shin = "ש", LamedHe = "ל", Vav = "ו", FinalMem = "ם";

    /// <summary>A system font with Arabic and Hebrew glyphs (Arial on Windows, DejaVu Sans on Linux), or null.</summary>
    private static TrueTypeFont? Font()
    {
        var path = TestHelpers.FindSystemFont();
        if (path == null) return null;
        var font = new TrueTypeFont(File.ReadAllBytes(path));
        return font.GetGlyphId(0x0628) != 0 && font.GetGlyphId(0x05E9) != 0 && font.Gsub != null ? font : null;
    }

    private static List<PositionedGlyph> Glyphs(ShapedLine line) =>
        line.Runs.SelectMany(r => r.Glyphs.Select(g => g with { X = g.X + r.Start })).OrderBy(g => g.X).ToList();

    [Fact]
    public void UnicodeData()
    {
        Assert.Equal(BidiClass.L, UnicodeTables.BidiClassOf('A'));
        Assert.Equal(BidiClass.R, UnicodeTables.BidiClassOf(0x05D0));
        Assert.Equal(BidiClass.AL, UnicodeTables.BidiClassOf(0x0627));
        Assert.Equal(BidiClass.EN, UnicodeTables.BidiClassOf('7'));
        Assert.Equal(BidiClass.AN, UnicodeTables.BidiClassOf(0x0663));
        Assert.Equal(BidiClass.NSM, UnicodeTables.BidiClassOf(0x064E));
        Assert.Equal(JoiningType.Dual, UnicodeTables.JoiningTypeOf(0x0628));
        Assert.Equal(JoiningType.Right, UnicodeTables.JoiningTypeOf(0x0627));
        Assert.Equal(JoiningType.Transparent, UnicodeTables.JoiningTypeOf(0x064E));
        Assert.Equal(JoiningType.None, UnicodeTables.JoiningTypeOf(' '));
        Assert.Equal(')', UnicodeTables.Mirror('('));
        Assert.Equal('x', UnicodeTables.Mirror('x'));
        Assert.Equal((')', true), UnicodeTables.Bracket('(')!.Value);
        Assert.Equal(UnicodeScript.Arabic, UnicodeTables.ScriptOf(0x0628));
        Assert.Equal(UnicodeScript.Hebrew, UnicodeTables.ScriptOf(0x05D0));
        Assert.Equal(UnicodeScript.Common, UnicodeTables.ScriptOf(' '));
        Assert.True(Shaper.NeedsShaping("abc " + Beh));
        Assert.True(Shaper.NeedsShaping("é"));
        Assert.False(Shaper.NeedsShaping("Grüße, naïve 123"));
    }

    [Fact]
    public void ArabicLettersTakeTheirJoiningForms()
    {
        if (Font() is not { } font) return;
        var line = Shaper.ShapeLine(font, Beh + Yeh + Teh, ligatures: false, kerning: false);
        var run = Assert.Single(line.Runs);
        Assert.True(run.RightToLeft);
        var glyphs = run.Glyphs; // visual order: the last letter is at the left
        Assert.Equal(new[] { 2, 1, 0 }, glyphs.Select(g => g.Cluster));
        Assert.NotEqual(font.GetGlyphId(0x0628), glyphs[2].Gid); // initial beh
        Assert.NotEqual(font.GetGlyphId(0x064A), glyphs[1].Gid); // medial yeh
        Assert.True(glyphs[0].X < glyphs[1].X && glyphs[1].X < glyphs[2].X);
        Assert.Equal(Teh, glyphs[0].Text);

        // lam + alef form one ligature glyph that stands for both letters
        var lamAlef = Shaper.ShapeLine(font, Lam + Alef, false, false);
        var ligature = Assert.Single(lamAlef.Runs[0].Glyphs);
        Assert.Equal(Lam + Alef, ligature.Text);
    }

    [Fact]
    public void MarksArePlacedOnTheirBase()
    {
        if (Font() is not { } font || font.Gpos == null) return;
        var line = Shaper.ShapeLine(font, Beh + Fatha, false, false);
        var glyphs = line.Runs[0].Glyphs;
        Assert.Equal(2, glyphs.Count);
        var mark = glyphs.Single(g => g.Text == Fatha);
        var letter = glyphs.Single(g => g.Text == Beh);
        Assert.NotEqual(0, mark.Y - letter.Y); // raised above the letter
        Assert.InRange(mark.X, letter.X - font.UnitsPerEm, letter.X + font.GetAdvanceWidth(letter.Gid));
        Assert.Equal(font.GetAdvanceWidth(letter.Gid), line.Width, 3); // the mark adds no width
    }

    [Fact]
    public void BidiRunsAndMirroring()
    {
        if (Font() is not { } font) return;
        // a Hebrew word in Latin text: three runs, the Hebrew letters reversed
        var line = Shaper.ShapeLine(font, "ab " + Shin + LamedHe + Vav + FinalMem + " cd", false, false);
        var glyphs = Glyphs(line);
        Assert.Equal("ab " + FinalMem + Vav + LamedHe + Shin + " cd", string.Concat(glyphs.Select(g => g.Text)));
        // brackets in right-to-left text are mirrored: "(" at the logical start shows as ")" at the right
        var rtl = Shaper.ShapeLine(font, "(" + Shin + ")", false, false);
        var shown = Glyphs(rtl);
        Assert.Equal(font.GetGlyphId('('), shown[0].Gid); // the logical ")" mirrored, at the left
        Assert.Equal(font.GetGlyphId(')'), shown[2].Gid);
        Assert.Equal(")", shown[0].Text);
    }

    private static string Extract(Action<PdfDocument, PdfPage> draw)
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage(new PdfRectangle(0, 0, 400, 200));
        draw(doc, page);
        using var reopened = PdfDocument.Open(doc.ToArray());
        return reopened.GetPage(1).ExtractText();
    }

    [Fact]
    public void ShapedTextIsWrittenAndExtractedInLogicalOrder()
    {
        var path = TestHelpers.FindSystemFont();
        if (path == null || Font() == null) return;
        var font = PdfTrueTypeFont.FromFile(path);
        string arabic = Alef + Lam + Beh + Yeh + Teh + " " + Lam + Alef;
        string mixed = "Price 12 " + Shin + LamedHe + Vav + FinalMem + " (" + Shin + ") ok";
        string marks = "Café résumé";
        var text = Extract((doc, page) =>
        {
            using var canvas = page.GetCanvas();
            canvas.BeginText().SetFont(font, 14).SetTextPosition(20, 160).ShowText(arabic).EndText();
            canvas.BeginText().SetFont(font, 14).SetTextPosition(20, 120).ShowText(mixed).EndText();
            canvas.BeginText().SetFont(font, 14).SetTextPosition(20, 80).ShowTextWithWordSpacing(marks, 3).EndText();
        });
        var lines = text.Split('\n');
        Assert.Equal(arabic, lines[0]);
        Assert.Equal(mixed, lines[1]);
        Assert.Equal(marks, lines[2]);
        // the measured width is the shaped width (lam-alef is one glyph)
        Assert.True(font.GetTextWidth(Lam + Alef, 10) < font.GetCharWidth(0x0644) / 100 + font.GetCharWidth(0x0627) / 100);
    }

    [Fact]
    public void RightToLeftParagraphs()
    {
        var path = TestHelpers.FindSystemFont();
        if (path == null || Font() == null) return;
        var font = PdfTrueTypeFont.FromFile(path);
        string sentence = string.Join(" ", Enumerable.Repeat(Shin + LamedHe + Vav + FinalMem, 12)) + " 2025.";
        var doc = PdfDocument.Create();
        var layout = new LayoutDocument(doc, new PdfRectangle(0, 0, 400, 200));
        var paragraph = new Paragraph { Font = font, FontSize = 14, Alignment = TextAlignment.Right };
        paragraph.Add(sentence[..20]);
        paragraph.Add(sentence[20..], font, 16); // a second style in the same line
        layout.Add(paragraph);
        layout.Close();
        using var reopened = PdfDocument.Open(doc.ToArray());
        var text = reopened.GetPage(1).ExtractText();
        Assert.Contains('\n', text); // several lines
        Assert.Equal(sentence, text.Replace("\n", " "));
    }

    [Fact]
    public void OptionalLigatures()
    {
        var path = TestHelpers.FindSystemFont();
        if (path == null) return;
        var plain = PdfTrueTypeFont.FromFile(path);
        var ligatures = PdfTrueTypeFont.FromFile(path);
        ligatures.Ligatures = true;
        var ttf = new TrueTypeFont(File.ReadAllBytes(path));
        int shapedCount = Shaper.ShapeLine(ttf, "office", ligatures: true, kerning: false).Runs[0].Glyphs.Count;
        Assert.InRange(shapedCount, 4, 6); // 4 or 5 glyphs if the font has ffi/fi ligatures
        var text = Extract((doc, page) =>
        {
            using var canvas = page.GetCanvas();
            canvas.BeginText().SetFont(ligatures, 14).SetTextPosition(20, 160).ShowText("office fjord").EndText();
            canvas.BeginText().SetFont(plain, 14).SetTextPosition(20, 120).ShowText("office fjord").EndText();
        });
        Assert.Equal("office fjord\noffice fjord", text);
    }
}

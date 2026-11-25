using System.Buffers.Binary;
using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Objects;
using MiPdf.Text;

namespace MiPdf.Tests.Text;

/// <summary>
/// Bold and italic words, decorations (lines under and through words, text markup) and links: <see cref="TextWord"/>
/// and the Markdown export. During development the export was checked on arXiv papers, IRS forms and ZUGFeRD invoices:
/// fraction bars, plot lines, form boxes and table rules are not taken for decorations.
/// </summary>
public class InlineFormattingTests
{
    private const double Size = 11;

    private static PdfDocument Reopen(PdfDocument doc) => PdfDocument.Open(doc.ToArray());

    /// <summary>A paragraph of two lines in which some words are bold, italic, underlined, struck out or linked.</summary>
    private static PdfDocument Sample()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage(new PdfRectangle(0, 0, 600, 800));
        using (var c = page.GetCanvas())
        {
            c.BeginText().SetFont(PdfStandardFont.Helvetica, Size).MoveText(72, 700)
                .ShowText("This is ").SetFont(PdfStandardFont.HelveticaBold, Size).ShowText("very important")
                .SetFont(PdfStandardFont.Helvetica, Size).ShowText(" and ").SetFont(PdfStandardFont.HelveticaOblique, Size).ShowText("emphasised")
                .SetFont(PdfStandardFont.Helvetica, Size).ShowText(", the old price is obsolete and this is underlined.")
                .EndText();
            c.BeginText().SetFont(PdfStandardFont.Helvetica, Size).MoveText(72, 686)
                .ShowText("Read the documentation for details. A remark that is marked and cancelled ends it.")
                .EndText();
        }
        var first = Reopen(doc);
        var p = first.GetPage(1);
        Rect(p, "obsolete", out var obsolete);
        Rect(p, "underlined.", out var underlined);
        using (var c = p.GetCanvas())
        {
            // a line through "obsolete" (at the middle of the lower-case letters) and one under "underlined"
            double baseline = obsolete.Bottom + 0.2 * Size;
            c.DrawLine(obsolete.Left, baseline + 0.3 * Size, obsolete.Right, baseline + 0.3 * Size, PdfColor.Black, 0.6);
            c.DrawLine(underlined.Left, baseline - 0.12 * Size, underlined.Right - 3, baseline - 0.12 * Size, PdfColor.Black, 0.6);
        }
        Rect(p, "documentation", out var documentation);
        Rect(p, "details.", out var details);
        Rect(p, "marked", out var marked);
        Rect(p, "cancelled", out var cancelled);
        p.AddLink(documentation, "https://example.com/docs");
        p.AddLink(details, "https://example.com/details");
        p.AddTextMarkup(TextMarkupType.Highlight, new[] { marked });
        p.AddTextMarkup(TextMarkupType.StrikeOut, new[] { cancelled });
        return Reopen(first);
    }

    private static void Rect(PdfPage page, string text, out PdfRectangle rect) => rect = Assert.Single(page.FindText(text));

    private static TextWord Word(PageLayout page, string text) => page.Blocks.SelectMany(b => b.Lines).SelectMany(l => l.Words).Single(w => w.Text == text);

    [Fact]
    public void WordsCarryTheirStyleDecorationsAndLinks()
    {
        using var doc = Sample();
        var page = PdfLayoutAnalyzer.Analyze(doc.GetPage(1));
        Assert.True(Word(page, "important").IsBold);
        Assert.True(Word(page, "emphasised,").IsItalic);
        Assert.False(Word(page, "price").IsBold || Word(page, "price").IsItalic);
        Assert.Equal(TextDecorations.StrikeOut, Word(page, "obsolete").Decorations);
        Assert.Equal(TextDecorations.Underline, Word(page, "underlined.").Decorations);
        Assert.Equal(TextDecorations.Highlight, Word(page, "marked").Decorations);
        Assert.Equal(TextDecorations.StrikeOut, Word(page, "cancelled").Decorations);
        Assert.Equal(TextDecorations.None, Word(page, "price").Decorations);
        Assert.Equal("https://example.com/docs", Word(page, "documentation").Link);
        Assert.Null(Word(page, "Read").Link);
        // plain text extraction knows the style, but not the decorations and links
        var plain = PdfTextExtractor.ExtractWords(doc.GetPage(1));
        Assert.True(plain.Single(w => w.Text == "important").IsBold);
        Assert.Equal(TextDecorations.None, plain.Single(w => w.Text == "obsolete").Decorations);
    }

    [Fact]
    public void MarkdownMarksStylesStrikeOutsAndLinks()
    {
        using var doc = Sample();
        string md = PdfLayoutExport.ToMarkdown(doc).Trim();
        Assert.Equal(
            "This is **very important** and *emphasised*, the old price is ~~obsolete~~ and this is underlined. " +
            "Read the [documentation](https://example.com/docs) for [details](https://example.com/details). " +
            "A remark that is marked and ~~cancelled~~ ends it.", md);

        string plain = PdfLayoutExport.ToMarkdown(PdfLayoutAnalyzer.Analyze(doc), new MarkdownExportOptions { InlineFormatting = false, Links = false }).Trim();
        Assert.StartsWith("This is very important and emphasised, the old price is obsolete and", plain);
        Assert.Contains("Read the documentation for details.", plain);
    }

    [Fact]
    public void TheStyleOfAWholeBlockIsNotMarked()
    {
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage(new PdfRectangle(0, 0, 600, 800)).GetCanvas())
        {
            c.DrawText("Terms", PdfStandardFont.HelveticaBold, 16, 72, 740);
            for (int i = 0; i < 4; i++)
                c.DrawText("All prices are net prices plus the statutory value added tax of the country.", PdfStandardFont.HelveticaBold, Size, 72, 700 - 14 * i);
            c.DrawText("An italic note on a line of its own.", PdfStandardFont.HelveticaOblique, Size, 72, 600);
        }
        string md = PdfLayoutExport.ToMarkdown(Reopen(doc));
        Assert.StartsWith("# Terms\n\nAll prices are net prices", md);
        Assert.DoesNotContain("*", md);
    }

    [Fact]
    public void FractionBarsAndTableRulesAreNoDecorations()
    {
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage(new PdfRectangle(0, 0, 600, 800)).GetCanvas())
        {
            // "result = value" with a fraction bar through "value" and the fraction's numerator and denominator around it
            c.DrawText("result = value", PdfStandardFont.Helvetica, Size, 72, 600);
            c.DrawText("a", PdfStandardFont.Helvetica, 8, 130, 606);
            c.DrawText("b", PdfStandardFont.Helvetica, 8, 130, 592);
            c.DrawLine(124, 603.3, 152, 603.3, PdfColor.Black, 0.5);
            // a word above a rule that runs on across the page
            c.DrawText("Total", PdfStandardFont.Helvetica, Size, 72, 500);
            c.DrawLine(72, 498.5, 520, 498.5, PdfColor.Black, 0.5);
        }
        var page = PdfLayoutAnalyzer.Analyze(Reopen(doc).GetPage(1));
        Assert.Equal(TextDecorations.None, Word(page, "value").Decorations);
        Assert.Equal(TextDecorations.None, Word(page, "Total").Decorations);
    }

    [Fact]
    public void BoldAndItalicFromTheEmbeddedProgramOfAFontWithAGenericName()
    {
        if (TestHelpers.FindSystemFont() is not { } path) return;
        byte[] Patched(bool bold)
        {
            var data = File.ReadAllBytes(path);
            int tables = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
            for (int i = 0; i < tables; i++)
            {
                int record = 12 + 16 * i, offset = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 8));
                string tag = System.Text.Encoding.ASCII.GetString(data, record, 4);
                if (tag == "OS/2")
                {
                    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 4), (ushort)(bold ? 700 : 400));
                    ushort selection = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 62));
                    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 62), (ushort)(bold ? (selection | 0x20) & ~0x40 : selection & ~0x20));
                }
                else if (tag == "head")
                {
                    ushort style = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 44));
                    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 44), (ushort)(bold ? style | 1 : style & ~1));
                }
            }
            return data;
        }
        foreach (bool bold in new[] { true, false })
        {
            var doc = PdfDocument.Create();
            var font = PdfTrueTypeFont.FromBytes(Patched(bold));
            using (var c = doc.AddPage(new PdfRectangle(0, 0, 400, 200)).GetCanvas()) c.DrawText("Generic name", font, 12, 20, 100);
            var opened = Reopen(doc);
            // a generic name and a descriptor without weight, as some generators write them
            var dict = (PdfDictionary)((PdfDictionary)opened.GetPage(1).Resources[PdfName.Font]!.Resolve()!).First().Value.Resolve()!;
            foreach (var d in new[] { dict, dict.GetArray(PdfName.DescendantFonts)?.GetDictionary(0) }.OfType<PdfDictionary>())
            {
                d[PdfName.BaseFont] = PdfName.Get("CIDFont+F1");
                if (d.GetDictionary(PdfName.FontDescriptor) is { } descriptor)
                {
                    descriptor[PdfName.FontName] = PdfName.Get("CIDFont+F1");
                    descriptor.Remove(PdfName.Get("FontWeight"));
                    descriptor.Set(PdfName.Get("Flags"), 32);
                    descriptor.Set(PdfName.Get("ItalicAngle"), 0);
                }
            }
            var letters = PdfTextExtractor.ExtractLetters(Reopen(opened).GetPage(1));
            Assert.All(letters.Where(l => l.Text != " "), l => Assert.Equal(bold, l.IsBold));
        }
    }
}

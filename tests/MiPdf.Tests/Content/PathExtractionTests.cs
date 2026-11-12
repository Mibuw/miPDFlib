using MiPdf.Content;
using MiPdf.Graphics;

namespace MiPdf.Tests.Content;

/// <summary>
/// <see cref="PdfGraphicsExtractor.ExtractPaths"/>. During development the paths of 2,800 pages (arXiv papers, IRS forms
/// and instructions, ZUGFeRD invoices; 700,000 paths) were compared with PdfPig 0.1.16: the same paths on every page,
/// with the same bounds, painting operators and fill colors (except one point-sized fill that PdfPig leaves out and
/// stroked paths without segments that PdfPig reports; spot colors converted differently).
/// </summary>
public class PathExtractionTests
{
    private static PdfPage Reopen(PdfDocument doc) => PdfDocument.Open(doc.ToArray()).GetPage(1);

    [Fact]
    public void PathsWithTheirSegmentsColorsAndLineStyle()
    {
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage(new PdfRectangle(0, 0, 600, 800)).GetCanvas())
        {
            // a filled rectangle
            c.SetFillColor(PdfColor.Rgb(1, 0, 0)).Rectangle(50, 600, 100, 50).Fill();
            // a stroked curve, drawn at twice the size: line width and dashes scale with it
            c.SaveState().Transform(2, 0, 0, 2, 200, 100)
                .SetStrokeColor(PdfColor.Cmyk(0, 0, 0, 1)).SetLineWidth(1.5).SetLineCap(LineCap.Round).SetLineJoin(LineJoin.Bevel)
                .SetDashPattern(3, 2)
                .MoveTo(0, 0).CurveTo(0, 100, 100, 100, 100, 0).Stroke()
                .RestoreState();
            // filled and stroked with the even-odd rule, half transparent, closed with h
            c.SetOpacity(0.5).SetFillColor(PdfColor.Gray(0.5)).SetStrokeColor(PdfColor.Blue)
                .MoveTo(400, 400).LineTo(500, 400).LineTo(450, 480).ClosePath().FillAndStroke(evenOdd: true);
        }
        var paths = PdfGraphicsExtractor.ExtractPaths(Reopen(doc));
        Assert.Equal(3, paths.Count);

        var rect = paths[0];
        Assert.True(rect.IsFilled);
        Assert.False(rect.IsStroked);
        Assert.Equal(PdfColor.Rgb(1, 0, 0), rect.FillColor);
        Assert.Null(rect.StrokeColor);
        Assert.Equal(new PdfRectangle(50, 600, 150, 650), rect.Bounds);
        var segments = Assert.Single(rect.Subpaths).Segments;
        Assert.Equal(new[] { PdfPathSegmentKind.MoveTo, PdfPathSegmentKind.LineTo, PdfPathSegmentKind.LineTo, PdfPathSegmentKind.LineTo, PdfPathSegmentKind.ClosePath },
            segments.Select(s => s.Kind));
        Assert.True(rect.Subpaths[0].IsClosed);

        var curve = paths[1];
        Assert.True(curve.IsStroked);
        Assert.False(curve.IsFilled);
        Assert.Equal(PdfColor.Cmyk(0, 0, 0, 1), curve.StrokeColor);
        Assert.Equal(3, curve.LineWidth, 6);
        Assert.Equal((LineCap.Round, LineJoin.Bevel), (curve.LineCap, curve.LineJoin));
        Assert.Equal(new[] { 6.0, 4.0 }, curve.DashArray);
        var bezier = curve.Subpaths[0].Segments[1];
        Assert.Equal(PdfPathSegmentKind.CurveTo, bezier.Kind);
        Assert.Equal((200.0, 300.0), bezier.Control1);
        Assert.Equal((400.0, 300.0), bezier.Control2);
        Assert.Equal((400.0, 100.0), bezier.Point);
        // the curve reaches 75 % of its control points' height (at t = 0.5), not the control points
        Assert.Equal(100 + 2 * 75, curve.Bounds.Top, 6);
        Assert.Equal((200, 100, 400), (curve.Bounds.Left, curve.Bounds.Bottom, curve.Bounds.Right));

        var triangle = paths[2];
        Assert.True(triangle.IsFilled && triangle.IsStroked);
        Assert.Equal(PdfFillRule.EvenOdd, triangle.FillRule);
        Assert.Equal((0.5, 0.5), (triangle.FillOpacity, triangle.StrokeOpacity));
        Assert.Equal(PdfColor.Gray(0.5), triangle.FillColor);
        Assert.Equal(PdfColor.Blue, triangle.StrokeColor);
        Assert.Equal(PdfPathSegmentKind.ClosePath, triangle.Subpaths[0].Segments[^1].Kind);
        Assert.Equal((400.0, 400.0), triangle.Subpaths[0].Segments[^1].Point);
    }

    [Fact]
    public void ClippingPathsOnlyWhenAskedFor()
    {
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage(new PdfRectangle(0, 0, 600, 800)).GetCanvas())
        {
            c.SaveState().Rectangle(100, 100, 200, 200).Clip();
            c.Rectangle(0, 0, 600, 800).Fill();
            c.RestoreState();
            c.MoveTo(10, 10).Stroke();   // a lone move paints nothing
        }
        var page = Reopen(doc);
        var painted = Assert.Single(PdfGraphicsExtractor.ExtractPaths(page));
        Assert.False(painted.IsClipping);

        var all = PdfGraphicsExtractor.ExtractPaths(page, new PathExtractionOptions { IncludeClippingPaths = true });
        Assert.Equal(2, all.Count);
        Assert.True(all[0].IsClipping);
        Assert.False(all[0].IsFilled || all[0].IsStroked);
        Assert.Equal(new PdfRectangle(100, 100, 300, 300), all[0].Bounds);
    }

    [Fact]
    public void PathsOfAnnotationAppearancesWhenAskedFor()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage(new PdfRectangle(0, 0, 600, 800));
        page.AddLineAnnotation(100, 150, 300, 150, PdfColor.Green, 2);
        var reopened = Reopen(doc);
        Assert.Empty(PdfGraphicsExtractor.ExtractPaths(reopened));

        var paths = PdfGraphicsExtractor.ExtractPaths(reopened, new PathExtractionOptions { IncludeAnnotations = true });
        var line = Assert.Single(paths, p => p.IsStroked && p.StrokeColor == PdfColor.Green);
        // the appearance is placed where the annotation is
        Assert.Equal(100, line.Bounds.Left, 3);
        Assert.Equal(300, line.Bounds.Right, 3);
        Assert.Equal(150, line.Bounds.Bottom, 3);
        Assert.Equal(2, line.LineWidth, 6);
    }

    [Fact]
    public void PathsInFormXObjectsAndLayers()
    {
        var doc = PdfDocument.Create();
        var stamp = new PdfFormXObject(doc, new PdfRectangle(0, 0, 100, 20));
        using (var c = stamp.GetCanvas()) c.DrawLine(0, 10, 100, 10, PdfColor.Red, 2);
        var draft = doc.AddLayer("Draft", visible: false);
        using (var c = doc.AddPage(new PdfRectangle(0, 0, 600, 800)).GetCanvas())
        {
            c.DrawXObject(stamp, 300, 300, width: 200, height: 40);
            c.Layer(draft, l => l.DrawRectangle(50, 50, 20, 20, fill: PdfColor.Black));
        }
        var page = Reopen(doc);
        var paths = PdfGraphicsExtractor.ExtractPaths(page);
        Assert.Equal(2, paths.Count);
        // the stamp's line placed and scaled by the form's position: twice as long, twice as thick
        var line = paths[0];
        Assert.Equal((300.0, 320.0), line.Subpaths[0].Segments[0].Point);
        Assert.Equal((500.0, 320.0), line.Subpaths[0].Segments[1].Point);
        Assert.Equal(4, line.LineWidth, 6);

        var visible = PdfGraphicsExtractor.ExtractPaths(page, new PathExtractionOptions { IncludeHiddenLayers = false });
        // the rectangle of the hidden layer is left out when only what a viewer shows is asked for
        Assert.Equal(PdfColor.Red, Assert.Single(visible).StrokeColor);
        Assert.True(paths[1].IsFilled);
    }
}

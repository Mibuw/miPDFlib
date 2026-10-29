using MiPdf.Graphics;
using MiPdf.Objects;
using MiPdf.Text;

namespace MiPdf.Content;

/// <summary>
/// Finds empty areas of a page, e.g. to place a signature or a stamp where it does not cover content. Occupied are
/// text (word boxes), images, painted vector graphics (bounding boxes) and – optionally – annotations. The result
/// consists of maximal free rectangles (they may overlap), largest first.
/// </summary>
public static class FreeSpaceFinder
{
    /// <summary>Maximum number of candidate rectangles kept during the search (protects against very complex pages).</summary>
    private const int MaxCandidates = 2000;

    public static IReadOnlyList<PdfRectangle> Find(PdfPage page, double minWidth, double minHeight, bool includeAnnotations = true)
    {
        var occupied = new List<PdfRectangle>();
        foreach (var word in PdfTextExtractor.ExtractWords(page)) occupied.Add(word.Bounds);
        new GraphicsWalker
        {
            Image = (_, ctm, _) =>
            {
                var c = new[] { ctm.Transform(0, 0), ctm.Transform(1, 0), ctm.Transform(0, 1), ctm.Transform(1, 1) };
                occupied.Add(new PdfRectangle(c.Min(p => p.X), c.Min(p => p.Y), c.Max(p => p.X), c.Max(p => p.Y)));
            },
            Path = box =>
            {
                // hairlines have no area: give them a minimal extent so they still separate free areas
                occupied.Add(new PdfRectangle(box.Left - 0.5, box.Bottom - 0.5, box.Right + 0.5, box.Top + 0.5));
            },
        }.Walk(page.GetContentBytes(), page.GetInheritable(PdfName.Resources) as PdfDictionary, Matrix.Identity);
        if (includeAnnotations)
            foreach (var annotation in page.Annotations)
                if (annotation.Subtype != "Popup" && (annotation.Flags & 2) == 0 && annotation.Rect.Width > 0) occupied.Add(annotation.Rect);
        return Find(page.CropBox, occupied, minWidth, minHeight);
    }

    /// <summary>Maximal free rectangles inside <paramref name="area"/> that do not intersect any occupied rectangle.</summary>
    public static IReadOnlyList<PdfRectangle> Find(PdfRectangle area, IEnumerable<PdfRectangle> occupied, double minWidth, double minHeight)
    {
        var free = new List<PdfRectangle> { area };
        foreach (var block in occupied)
        {
            if (block.Right <= area.Left || block.Left >= area.Right || block.Top <= area.Bottom || block.Bottom >= area.Top) continue;
            var next = new List<PdfRectangle>(free.Count + 8);
            foreach (var f in free)
            {
                if (block.Left >= f.Right || block.Right <= f.Left || block.Bottom >= f.Top || block.Top <= f.Bottom)
                {
                    next.Add(f);
                    continue;
                }
                // up to four maximal fragments around the block
                if (block.Left > f.Left) next.Add(new PdfRectangle(f.Left, f.Bottom, block.Left, f.Top));
                if (block.Right < f.Right) next.Add(new PdfRectangle(block.Right, f.Bottom, f.Right, f.Top));
                if (block.Bottom > f.Bottom) next.Add(new PdfRectangle(f.Left, f.Bottom, f.Right, block.Bottom));
                if (block.Top < f.Top) next.Add(new PdfRectangle(f.Left, block.Top, f.Right, f.Top));
            }
            free = RemoveContained(next.Where(r => r.Width >= minWidth && r.Height >= minHeight).ToList());
            if (free.Count > MaxCandidates) free = free.OrderByDescending(r => r.Width * r.Height).Take(MaxCandidates).ToList();
            if (free.Count == 0) break;
        }
        return free.OrderByDescending(r => r.Width * r.Height).ThenByDescending(r => r.Top).ThenBy(r => r.Left).ToList();
    }

    private static List<PdfRectangle> RemoveContained(List<PdfRectangle> rects)
    {
        rects.Sort((a, b) => (b.Width * b.Height).CompareTo(a.Width * a.Height));
        var result = new List<PdfRectangle>(rects.Count);
        foreach (var r in rects)
        {
            bool contained = false;
            foreach (var k in result)
            {
                if (r.Left >= k.Left - 1e-6 && r.Right <= k.Right + 1e-6 && r.Bottom >= k.Bottom - 1e-6 && r.Top <= k.Top + 1e-6)
                {
                    contained = true;
                    break;
                }
            }
            if (!contained) result.Add(r);
        }
        return result;
    }
}

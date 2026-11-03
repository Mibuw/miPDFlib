using MiPdf.IO;
using MiPdf.Objects;

namespace MiPdf.Tests.Core;

/// <summary>Stream data of opened files is shared with the file data instead of copied; saving writes it unchanged.</summary>
public class StreamDataTests
{
    private static byte[] CreateWithImage()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        var image = new PdfStream();
        image.SetRawBytes(Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray());
        image[PdfName.Type] = PdfName.XObject;
        image[PdfName.Subtype] = PdfName.Get("Image");
        image[PdfName.Filter] = PdfName.Get("DCTDecode");
        var reference = doc.AddObject(image);
        page.Dictionary.GetDictionary(PdfName.Resources)![PdfName.XObject] = new PdfDictionary { [PdfName.Get("Im0")] = reference };
        return doc.ToArray();
    }

    private static PdfStream Image(PdfDocument doc) =>
        (PdfStream)doc.Pages[0].Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.XObject)!.Get(PdfName.Get("Im0"))!.Resolve()!;

    [Fact]
    public void ChangingTheRawBytesInPlaceIsSavedIncrementally()
    {
        var bytes = CreateWithImage();
        var doc = PdfDocument.Open(bytes);
        Image(doc).RawBytes[0] = 0xAB;
        var saved = doc.ToArray(new PdfSaveOptions { Incremental = true });

        Assert.Equal(0, Image(PdfDocument.Open(bytes)).RawBytes[0]);   // the input array is not modified
        Assert.Equal(0xAB, Image(PdfDocument.Open(saved)).RawBytes[0]);
    }

    [Fact]
    public void UnchangedStreamsAreNotPartOfAnIncrementalUpdate()
    {
        var bytes = CreateWithImage();
        var doc = PdfDocument.Open(bytes);
        _ = Image(doc).GetBytes();
        doc.Info.Title = "Changed";
        var saved = doc.ToArray(new PdfSaveOptions { Incremental = true });
        Assert.True(saved.Length - bytes.Length < 2000, $"update of {saved.Length - bytes.Length} bytes");
        Assert.Equal(bytes, saved.AsSpan(0, bytes.Length).ToArray());
    }

    [Fact]
    public void ClonesAndImportedCopiesDoNotChangeTheOriginal()
    {
        var bytes = CreateWithImage();
        var doc = PdfDocument.Open(bytes);
        var image = Image(doc);
        var clone = (PdfStream)image.Clone();
        clone.RawBytes[1] = 0xCD;

        var target = PdfDocument.Create();
        target.ImportPage(doc.Pages[0]);
        var imported = Image(target);
        imported.RawBytes[2] = 0xEF;

        Assert.Equal(7, image.RawBytes[1]);
        Assert.Equal(14, image.RawBytes[2]);
        Assert.Equal(image.RawBytes.Length, imported.RawBytes.Length);
        Assert.Equal(0xEF, Image(PdfDocument.Open(target.ToArray())).RawBytes[2]);
    }

    [Fact]
    public void ToArrayAndSaveToAStreamWriteTheSameIncrementalUpdate()
    {
        var bytes = CreateWithImage();
        // a file without a final line break gets one before the update
        var trimmed = bytes.AsSpan(0, bytes.Length - 1).ToArray();
        foreach (var input in new[] { bytes, trimmed })
        {
            var options = new PdfSaveOptions { Incremental = true, UpdateModificationDate = false };
            var a = PdfDocument.Open(input);
            a.Info.Title = "Changed";
            var array = a.ToArray(options);
            var b = PdfDocument.Open(input);
            b.Info.Title = "Changed";
            using var ms = new MemoryStream();
            b.Save(ms, options);

            Assert.Equal(ms.Length, array.Length);
            Assert.Equal(input, array.AsSpan(0, input.Length).ToArray());
            Assert.Equal("Changed", PdfDocument.Open(array).Info.Title);
        }
    }

    [Fact]
    public void SavingOverTheSourceFileLeavesNoTemporaryFiles()
    {
        var dir = Directory.CreateTempSubdirectory("mipdf-save-");
        try
        {
            var path = Path.Combine(dir.FullName, "doc.pdf");
            File.WriteAllBytes(path, CreateWithImage());
            var doc = PdfDocument.Open(path);
            doc.Info.Title = "Incremental";
            doc.Save(path, new PdfSaveOptions { Incremental = true });
            doc = PdfDocument.Open(path);
            doc.Info.Title = "Full";
            doc.Save(path);

            Assert.Equal("Full", PdfDocument.Open(path).Info.Title);
            Assert.Equal(new[] { "doc.pdf" }, dir.GetFiles().Select(f => f.Name));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void OpensFromSeekableAndNonSeekableStreams()
    {
        var bytes = CreateWithImage();
        using var seekable = new MemoryStream(bytes);
        seekable.Position = 0;
        Assert.Equal(1, PdfDocument.Open(seekable).PageCount);
        using var nonSeekable = new NonSeekableStream(bytes);
        Assert.Equal(5000, Image(PdfDocument.Open(nonSeekable)).RawBytes.Length);
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(count, 1000), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

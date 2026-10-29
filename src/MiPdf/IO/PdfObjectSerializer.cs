using System.Text;
using MiPdf.Objects;

namespace MiPdf.IO;

/// <summary>Encrypts strings and streams of an indirect object during writing.</summary>
internal interface IObjectEncryptor
{
    byte[] EncryptString(byte[] data, int objectNumber, int generation);

    byte[] EncryptStream(byte[] data, int objectNumber, int generation, PdfStream stream);
}

/// <summary>A byte output with position tracking.</summary>
internal sealed class PdfOutput
{
    private readonly Stream _stream;
    private readonly long _start;

    public PdfOutput(Stream stream)
    {
        _stream = stream;
        _start = stream.CanSeek ? stream.Position : 0;
    }

    public long Position { get; private set; }

    public Stream BaseStream => _stream;

    public PdfOutput Write(string ascii)
    {
        Span<byte> buffer = ascii.Length <= 256 ? stackalloc byte[ascii.Length] : new byte[ascii.Length];
        for (int i = 0; i < ascii.Length; i++) buffer[i] = (byte)ascii[i];
        Write(buffer);
        return this;
    }

    public PdfOutput Write(ReadOnlySpan<byte> bytes)
    {
        _stream.Write(bytes);
        Position += bytes.Length;
        return this;
    }

    public PdfOutput WriteByte(byte b)
    {
        _stream.WriteByte(b);
        Position++;
        return this;
    }

    public PdfOutput WriteLine(string ascii = "") => Write(ascii).WriteByte((byte)'\n');
}

/// <summary>Serializes PDF objects into their file syntax.</summary>
internal sealed class PdfObjectSerializer
{
    /// <summary>Maps a reference to the object number/generation used in the output.</summary>
    public Func<PdfReference, (int Number, int Generation)?>? MapReference { get; init; }

    /// <summary>When false, strings are never encrypted (objects inside object streams).</summary>
    public bool EncryptStrings { get; init; } = true;

    /// <summary>Called for streams found as direct objects; must return the indirect number to write.</summary>
    public Func<PdfStream, (int Number, int Generation)>? IndirectStream { get; init; }

    public IObjectEncryptor? Encryptor { get; init; }

    /// <summary>Objects that must be written unencrypted (e.g. the encryption dictionary).</summary>
    public Func<int, bool>? IsUnencryptedObject { get; init; }

    private int _currentNumber;
    private int _currentGeneration;
    private bool _encryptCurrent;

    public static string ToDebugString(PdfObject obj)
    {
        using var ms = new MemoryStream();
        var serializer = new PdfObjectSerializer();
        serializer.Write(obj, new PdfOutput(ms), debug: true);
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    /// <summary>Writes <c>n g obj ... endobj</c>, including stream data.</summary>
    public void WriteIndirect(PdfOutput output, int number, int generation, PdfObject obj)
    {
        _currentNumber = number;
        _currentGeneration = generation;
        _encryptCurrent = Encryptor != null && EncryptStrings && !(IsUnencryptedObject?.Invoke(number) ?? false);
        output.Write($"{number} {generation} obj\n");
        if (obj is PdfStream stream)
        {
            byte[] data = stream.RawBytes;
            if (Encryptor != null && !(IsUnencryptedObject?.Invoke(number) ?? false) && !IsXrefStream(stream))
                data = Encryptor.EncryptStream(data, number, generation, stream);
            WriteDictionaryBody(stream, output, data.Length);
            output.Write("\nstream\n").Write(data).Write("\nendstream");
        }
        else
        {
            Write(obj, output);
        }
        output.Write("\nendobj\n");
        _encryptCurrent = false;
    }

    private static bool IsXrefStream(PdfStream s) => s.GetName(PdfName.Type) == PdfName.XRef;

    public void Write(PdfObject obj, PdfOutput output) => Write(obj, output, debug: false);

    private void Write(PdfObject obj, PdfOutput output, bool debug)
    {
        switch (obj)
        {
            case PdfNull:
                output.Write("null");
                break;
            case PdfBoolean b:
                output.Write(b.Value ? "true" : "false");
                break;
            case PdfNumber n:
                output.Write(n.ToPdfString());
                break;
            case PdfName name:
                WriteName(name, output);
                break;
            case PdfString s:
                WriteString(s, output);
                break;
            case PdfArray a:
                output.WriteByte((byte)'[');
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0) output.WriteByte((byte)' ');
                    Write(a[i], output, debug);
                }
                output.WriteByte((byte)']');
                break;
            case PdfStream stream:
                if (debug)
                {
                    WriteDictionaryBody(stream, output, stream.RawBytes.Length, debug);
                    output.Write($" stream[{stream.RawBytes.Length} bytes]");
                }
                else if (IndirectStream != null)
                {
                    var (num, gen) = IndirectStream(stream);
                    output.Write($"{num} {gen} R");
                }
                else throw new PdfException("Streams must be indirect objects.");
                break;
            case PdfDictionary d:
                WriteDictionaryBody(d, output, -1, debug);
                break;
            case PdfReference r:
                var mapped = MapReference != null ? MapReference(r) : (r.ObjectNumber, r.Generation);
                if (mapped is { } m) output.Write($"{m.Number} {m.Generation} R");
                else output.Write("null"); // dangling reference

                break;
            default:
                throw new PdfException($"Cannot serialize {obj.GetType().Name}.");
        }
    }

    private void WriteDictionaryBody(PdfDictionary dict, PdfOutput output, long streamLength, bool debug = false)
    {
        output.Write("<<");
        // signature /Contents are never encrypted (ISO 32000-2, 7.6.2)
        bool isSignature = _encryptCurrent && dict.ContainsKey(PdfName.ByteRange) &&
                           dict.GetName(PdfName.Type) is { } t && (t == PdfName.Sig || t.Value == "DocTimeStamp");
        foreach (var (key, value) in dict)
        {
            if (streamLength >= 0 && key == PdfName.Length) continue;
            WriteName(key, output);
            if (value is PdfStream || value is not (PdfDictionary or PdfArray or PdfString or PdfName)) output.WriteByte((byte)' ');
            if (isSignature && key == PdfName.Contents)
            {
                _encryptCurrent = false;
                Write(value, output, debug);
                _encryptCurrent = true;
            }
            else Write(value, output, debug);
        }
        if (streamLength >= 0) output.Write($"/Length {streamLength}");
        output.Write(">>");
    }

    public static void WriteName(PdfName name, PdfOutput output)
    {
        output.WriteByte((byte)'/');
        var bytes = Encoding.UTF8.GetBytes(name.Value);
        foreach (byte b in bytes)
        {
            if (b < 0x21 || b > 0x7E || b == '#' || PdfLexer.IsDelimiter(b))
                output.Write($"#{b:X2}");
            else output.WriteByte(b);
        }
    }

    private void WriteString(PdfString s, PdfOutput output)
    {
        var bytes = s.Bytes;
        if (_encryptCurrent) bytes = Encryptor!.EncryptString(bytes, _currentNumber, _currentGeneration);
        bool hex = s.IsHex || _encryptCurrent || !IsMostlyPrintable(bytes);
        if (hex)
        {
            output.WriteByte((byte)'<').Write(Convert.ToHexString(bytes)).WriteByte((byte)'>');
            return;
        }
        output.WriteByte((byte)'(');
        foreach (byte b in bytes)
        {
            switch (b)
            {
                case (byte)'(':
                case (byte)')':
                case (byte)'\\':
                    output.WriteByte((byte)'\\').WriteByte(b);
                    break;
                case (byte)'\r':
                    output.Write("\\r");
                    break;
                case (byte)'\n':
                    output.Write("\\n");
                    break;
                default:
                    output.WriteByte(b);
                    break;
            }
        }
        output.WriteByte((byte)')');
    }

    private static bool IsMostlyPrintable(byte[] bytes)
    {
        foreach (byte b in bytes)
            if (b < 0x20 && b is not ((byte)'\r' or (byte)'\n' or (byte)'\t')) return false;
        return true;
    }
}

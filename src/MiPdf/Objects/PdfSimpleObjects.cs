using System.Globalization;

namespace MiPdf.Objects;

/// <summary>The PDF <c>null</c> object.</summary>
public sealed class PdfNull : PdfObject
{
    public static readonly PdfNull Instance = new();

    private PdfNull() { }

    public override PdfObjectType ObjectType => PdfObjectType.Null;

    public override PdfObject Clone() => this;
}

/// <summary>A PDF boolean (<c>true</c>/<c>false</c>).</summary>
public sealed class PdfBoolean : PdfObject
{
    public static readonly PdfBoolean True = new(true);
    public static readonly PdfBoolean False = new(false);

    private PdfBoolean(bool value) => Value = value;

    public bool Value { get; }

    public override PdfObjectType ObjectType => PdfObjectType.Boolean;

    public static PdfBoolean Get(bool value) => value ? True : False;

    public override PdfObject Clone() => this;

    public override bool Equals(object? obj) => obj is PdfBoolean b && b.Value == Value;

    public override int GetHashCode() => Value.GetHashCode();
}

/// <summary>A PDF numeric object; either an integer or a real number.</summary>
public sealed class PdfNumber : PdfObject
{
    private readonly long _integer;
    private readonly double _real;

    public PdfNumber(long value)
    {
        _integer = value;
        _real = value;
        IsInteger = true;
    }

    public PdfNumber(int value) : this((long)value) { }

    public PdfNumber(double value)
    {
        _real = value;
        _integer = double.IsFinite(value) ? (long)Math.Round(value) : 0;
        IsInteger = false;
    }

    public override PdfObjectType ObjectType => PdfObjectType.Number;

    /// <summary>True if the number was created or parsed as an integer.</summary>
    public bool IsInteger { get; }

    public double Value => _real;

    public long LongValue => IsInteger ? _integer : (long)_real;

    public int IntValue => (int)Math.Clamp(LongValue, int.MinValue, int.MaxValue);

    public override PdfObject Clone() => this; // immutable

    public override bool Equals(object? obj) =>
        obj is PdfNumber n && (IsInteger && n.IsInteger ? n._integer == _integer : n._real.Equals(_real));

    public override int GetHashCode() => IsInteger ? _integer.GetHashCode() : _real.GetHashCode();

    public static implicit operator PdfNumber(int value) => new(value);

    public static implicit operator PdfNumber(double value) => new(value);

    internal string ToPdfString() => IsInteger ? _integer.ToString(CultureInfo.InvariantCulture) : FormatReal(_real);

    /// <summary>Formats a real without exponent and with at most 6 fractional digits.</summary>
    internal static string FormatReal(double value)
    {
        if (!double.IsFinite(value)) return "0";
        if (Math.Abs(value) < 0.000001) return "0";
        if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        var s = value.ToString("0.######", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }
}

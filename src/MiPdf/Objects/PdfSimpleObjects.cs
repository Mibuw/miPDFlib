using System.Globalization;

namespace MiPdf.Objects;

/// <summary>The PDF <c>null</c> object.</summary>
public sealed class PdfNull : PdfObject
{
    /// <summary>The single <c>null</c> instance.</summary>
    public static readonly PdfNull Instance = new();

    private PdfNull() { }

    /// <inheritdoc/>
    public override PdfObjectType ObjectType => PdfObjectType.Null;

    /// <summary>Returns this instance (the object is a singleton).</summary>
    public override PdfObject Clone() => this;
}

/// <summary>A PDF boolean (<c>true</c>/<c>false</c>).</summary>
public sealed class PdfBoolean : PdfObject
{
    /// <summary>The <c>true</c> instance.</summary>
    public static readonly PdfBoolean True = new(true);
    /// <summary>The <c>false</c> instance.</summary>
    public static readonly PdfBoolean False = new(false);

    private PdfBoolean(bool value) => Value = value;

    /// <summary>The boolean value.</summary>
    public bool Value { get; }

    /// <inheritdoc/>
    public override PdfObjectType ObjectType => PdfObjectType.Boolean;

    /// <summary>Returns the shared <see cref="True"/> or <see cref="False"/> instance.</summary>
    public static PdfBoolean Get(bool value) => value ? True : False;

    /// <summary>Returns this instance (the object is immutable).</summary>
    public override PdfObject Clone() => this;

    /// <summary>True if <paramref name="obj"/> is a <see cref="PdfBoolean"/> with the same value.</summary>
    public override bool Equals(object? obj) => obj is PdfBoolean b && b.Value == Value;

    /// <inheritdoc/>
    public override int GetHashCode() => Value.GetHashCode();
}

/// <summary>A PDF numeric object; either an integer or a real number.</summary>
public sealed class PdfNumber : PdfObject
{
    private readonly long _integer;
    private readonly double _real;

    /// <summary>Creates an integer number.</summary>
    public PdfNumber(long value)
    {
        _integer = value;
        _real = value;
        IsInteger = true;
    }

    /// <summary>Creates an integer number.</summary>
    public PdfNumber(int value) : this((long)value) { }

    /// <summary>Creates a real number.</summary>
    public PdfNumber(double value)
    {
        _real = value;
        _integer = double.IsFinite(value) ? (long)Math.Round(Math.Clamp(value, -9e18, 9e18)) : 0;
        IsInteger = false;
    }

    /// <inheritdoc/>
    public override PdfObjectType ObjectType => PdfObjectType.Number;

    /// <summary>True if the number was created or parsed as an integer.</summary>
    public bool IsInteger { get; }

    /// <summary>The value as <see cref="double"/>.</summary>
    public double Value => _real;

    /// <summary>The value as <see cref="long"/>; a real number is rounded to the nearest integer (as by the
    /// constructor).</summary>
    public long LongValue => _integer;

    /// <summary>The value as <see cref="int"/>, rounded like <see cref="LongValue"/> and clamped to the <see cref="int"/> range.</summary>
    public int IntValue => (int)Math.Clamp(LongValue, int.MinValue, int.MaxValue);

    /// <summary>Returns this instance (the object is immutable).</summary>
    public override PdfObject Clone() => this; // immutable

    /// <summary>
    /// True if <paramref name="obj"/> is a <see cref="PdfNumber"/> with the same value. Two integers are compared
    /// as integers, otherwise the real values are compared.
    /// </summary>
    public override bool Equals(object? obj) =>
        obj is PdfNumber n && (IsInteger && n.IsInteger ? n._integer == _integer : n._real.Equals(_real));

    /// <inheritdoc/>
    public override int GetHashCode() => IsInteger ? _integer.GetHashCode() : _real.GetHashCode();

    /// <summary>Converts an <see cref="int"/> to an integer number.</summary>
    public static implicit operator PdfNumber(int value) => new(value);

    /// <summary>Converts a <see cref="double"/> to a real number.</summary>
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

using System.Globalization;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Engine;

/// <summary>
/// Distinguishes "not present" from "explicit nil" from "blank string" from "zero" per
/// section 3. Adapters and functions must respect these distinctions.
/// </summary>
public enum ValuePresence
{
    /// <summary>Field or cell is missing.</summary>
    Missing,
    /// <summary>Value is present and non-null.</summary>
    Present,
    /// <summary>Value is explicitly nil (e.g. xbrli:nil).</summary>
    ExplicitNil,
    /// <summary>Text field is blank or whitespace only.</summary>
    Blank
}

/// <summary>
/// Runtime value flowing through ports. Deterministic and typed. Exact decimal
/// preservation is required for financial data (per section 3: bounded exact-decimal
/// domain). Values carry their original lexical form when precision would be lost.
/// </summary>
public sealed class DataValue : IEquatable<DataValue>
{
    public PortShape Shape { get; }
    public ValuePresence Presence { get; }
    public object? Raw { get; }

    /// <summary>Original lexical form for numeric inputs when culture/rounding matters.</summary>
    public string? LexicalSource { get; }

    /// <summary>Optional source cell for provenance. Populated by adapters on read.</summary>
    public XBRLMappingStudio.Diagnostics.SourceReference? Source { get; }

    private DataValue(PortShape shape, ValuePresence presence, object? raw,
        string? lexical = null,
        XBRLMappingStudio.Diagnostics.SourceReference? src = null)
    {
        Shape = shape;
        Presence = presence;
        Raw = raw;
        LexicalSource = lexical;
        Source = src;
    }

    public static DataValue Missing(PortShape shape = PortShape.String)
        => new(shape, ValuePresence.Missing, null);

    public static DataValue ExplicitNil(PortShape shape)
        => new(shape, ValuePresence.ExplicitNil, null);

    public static DataValue Blank()
        => new(PortShape.String, ValuePresence.Blank, "");

    public static DataValue DecimalValue(decimal d,
        XBRLMappingStudio.Diagnostics.SourceReference? src = null,
        string? lexical = null)
        => new(PortShape.Decimal, ValuePresence.Present, d, lexical, src);

    public static DataValue IntegerValue(long l,
        XBRLMappingStudio.Diagnostics.SourceReference? src = null)
        => new(PortShape.Integer, ValuePresence.Present, l, src: src);

    public static DataValue StringValue(string s,
        XBRLMappingStudio.Diagnostics.SourceReference? src = null)
    {
        if (s.Length == 0 || string.IsNullOrWhiteSpace(s))
            return new(PortShape.String, ValuePresence.Blank, s, src: src);
        return new(PortShape.String, ValuePresence.Present, s, src: src);
    }

    public static DataValue BoolValue(bool b)
        => new(PortShape.Boolean, ValuePresence.Present, b);

    public static DataValue DateValue(DateOnly d)
        => new(PortShape.Date, ValuePresence.Present, d);

    public static DataValue InstantValue(DateTimeOffset d)
        => new(PortShape.Instant, ValuePresence.Present, d);

    public static DataValue Record(IReadOnlyDictionary<string, DataValue> fields)
        => new(PortShape.Record, ValuePresence.Present, fields);

    public static DataValue Collection(IReadOnlyList<DataValue> items)
        => new(PortShape.Record, ValuePresence.Present, items);

    /// <summary>
    /// Construct an opaque value with a caller-provided payload and shape. Used by
    /// nodes that carry profile-specific structures such as period payloads.
    /// </summary>
    public static DataValue Opaque(PortShape shape, object payload,
        XBRLMappingStudio.Diagnostics.SourceReference? src = null)
        => new(shape, ValuePresence.Present, payload, src: src);

    public bool IsMissing => Presence == ValuePresence.Missing;
    public bool IsNil => Presence == ValuePresence.ExplicitNil;

    public decimal AsDecimal()
    {
        if (Presence != ValuePresence.Present || Raw is null)
            throw new InvalidOperationException("Cannot read missing/nil value as decimal.");
        return Raw switch
        {
            decimal d => d,
            long l => l,
            int i => i,
            string s => decimal.Parse(s, CultureInfo.InvariantCulture),
            _ => throw new InvalidCastException($"Cannot convert {Raw.GetType()} to decimal.")
        };
    }

    public string AsString()
    {
        if (Presence is ValuePresence.Missing or ValuePresence.ExplicitNil)
            return "";
        return Raw?.ToString() ?? "";
    }

    public bool AsBoolean()
    {
        if (Presence != ValuePresence.Present || Raw is null) return false;
        return Raw is bool b ? b : bool.Parse(Raw.ToString() ?? "false");
    }

    public bool Equals(DataValue? other)
    {
        if (other is null) return false;
        if (Shape != other.Shape || Presence != other.Presence) return false;
        return Equals(Raw, other.Raw);
    }

    public override bool Equals(object? obj) => Equals(obj as DataValue);

    public override int GetHashCode() => HashCode.Combine(Shape, Presence, Raw);

    public override string ToString()
        => Presence switch
        {
            ValuePresence.Missing => "<missing>",
            ValuePresence.ExplicitNil => "<nil>",
            ValuePresence.Blank => "<blank>",
            _ => Raw?.ToString() ?? "<null>"
        };
}

/// <summary>
/// Raised when an exact-decimal domain limit is exceeded (section 3 numeric bounds).
/// </summary>
public sealed class NumericOverflowException : Exception
{
    public NumericOverflowException(string message) : base(message) { }
}

/// <summary>
/// Raised when a transformation is requested without a required culture or rounding policy.
/// </summary>
public sealed class UndefinedConversionException : Exception
{
    public UndefinedConversionException(string message) : base(message) { }
}

/// <summary>
/// Raised when a Compare node is dispatched with an unsupported operator string.
/// </summary>
public sealed class InvalidCompareOperatorException : Exception
{
    public string Operator { get; }
    public InvalidCompareOperatorException(string op)
        : base($"Invalid comparison operator '{op}'. Expected one of Eq/Ne/Lt/Le/Gt/Ge.")
    { Operator = op; }
}

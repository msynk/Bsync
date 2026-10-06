using System.Globalization;

namespace Bsync.Storage;

/// <summary>
/// A declared secondary index of a collection (ADR-018): a named, versioned function of a document whose value orders and
/// selects documents without loading the whole collection. Stores that support indexes keep the encoded value of every
/// visible, live document next to the record, in the same transaction; others evaluate queries in memory.
/// </summary>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
public abstract class SyncIndex<TDocument>
    where TDocument : class, ISyncEntity
{
    private protected SyncIndex(string name, int version)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64 || !name.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            throw new ArgumentException("An index name is 1 to 64 ASCII letters, digits, '-', '_' or '.'.", nameof(name));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        Name = name;
        Version = version;
    }

    /// <summary>The index name, unique within a collection.</summary>
    public string Name { get; }

    /// <summary>
    /// The version of the value function. Change it whenever the function's meaning changes, so stores rebuild the stored
    /// keys; a changed function under the same version leaves stale keys (this is not detected).
    /// </summary>
    public int Version { get; }

    /// <summary>The encoded key of <paramref name="document"/> (see <see cref="SyncIndexKey"/>).</summary>
    public abstract string KeyOf(TDocument document);

    /// <summary>Declares an index on <paramref name="value"/>, which must be a pure function of the document.</summary>
    /// <typeparam name="TValue">
    /// <see cref="string"/>, <see cref="bool"/>, <see cref="char"/>, an integer type, <see cref="float"/>,
    /// <see cref="double"/>, <see cref="decimal"/>, <see cref="DateTimeOffset"/>, <see cref="DateTime"/>,
    /// <see cref="DateOnly"/>, <see cref="TimeOnly"/>, <see cref="TimeSpan"/>, <see cref="Guid"/>, an enum, or a nullable
    /// of one of them.
    /// </typeparam>
    public static SyncIndex<TDocument, TValue> Create<TValue>(string name, Func<TDocument, TValue> value, int version = 1)
    {
        ArgumentNullException.ThrowIfNull(value);
        SyncIndexKey.EnsureSupported(typeof(TValue));
        return new SyncIndex<TDocument, TValue>(name, version, value);
    }
}

/// <summary>A declared index over values of type <typeparamref name="TValue"/>; builds typed queries.</summary>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
/// <typeparam name="TValue">The indexed value type.</typeparam>
public sealed class SyncIndex<TDocument, TValue> : SyncIndex<TDocument>
    where TDocument : class, ISyncEntity
{
    private readonly Func<TDocument, TValue> _value;

    internal SyncIndex(string name, int version, Func<TDocument, TValue> value)
        : base(name, version) => _value = value;

    /// <inheritdoc />
    public override string KeyOf(TDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return SyncIndexKey.Encode(_value(document));
    }

    /// <summary>Every live document, in index order (ascending; equal values by id).</summary>
    public SyncIndexQuery<TDocument> All() => new(this, null, false, null, false, false);

    /// <summary>Documents whose value equals <paramref name="value"/>.</summary>
    public SyncIndexQuery<TDocument> Equal(TValue value)
    {
        var key = SyncIndexKey.Encode(value);
        return new(this, key, false, key, false, false);
    }

    /// <summary>Documents whose value is between <paramref name="from"/> and <paramref name="to"/>, both included.</summary>
    public SyncIndexQuery<TDocument> Between(TValue from, TValue to) => new(this, SyncIndexKey.Encode(from), false, SyncIndexKey.Encode(to), false, false);

    /// <summary>Documents whose value is at least <paramref name="from"/> (or above it, with <paramref name="exclusive"/>).</summary>
    public SyncIndexQuery<TDocument> From(TValue from, bool exclusive = false) => new(this, SyncIndexKey.Encode(from), exclusive, null, false, false);

    /// <summary>Documents whose value is below <paramref name="to"/> (or at most it, with <paramref name="inclusive"/>).</summary>
    public SyncIndexQuery<TDocument> Before(TValue to, bool inclusive = false) => new(this, null, false, SyncIndexKey.Encode(to), !inclusive, false);
}

/// <summary>
/// A range of one index, in ascending or descending order (equal keys by id, in the same direction). Bounds are encoded
/// keys; build queries with <see cref="SyncIndex{TDocument, TValue}"/>.
/// </summary>
/// <param name="Index">The index.</param>
/// <param name="Lower">The lower bound, or <see langword="null"/> for none.</param>
/// <param name="LowerExclusive">Whether the lower bound itself is excluded.</param>
/// <param name="Upper">The upper bound, or <see langword="null"/> for none.</param>
/// <param name="UpperExclusive">Whether the upper bound itself is excluded.</param>
/// <param name="IsDescending">Whether the order is descending.</param>
public sealed record SyncIndexQuery<TDocument>(
    SyncIndex<TDocument> Index,
    string? Lower,
    bool LowerExclusive,
    string? Upper,
    bool UpperExclusive,
    bool IsDescending)
    where TDocument : class, ISyncEntity
{
    /// <summary>The same range in descending order.</summary>
    public SyncIndexQuery<TDocument> Descending() => this with { IsDescending = true };

    /// <summary>Whether <paramref name="key"/> is within the range.</summary>
    public bool Contains(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (Lower is not null)
        {
            var c = string.CompareOrdinal(key, Lower);
            if (c < 0 || (c == 0 && LowerExclusive))
            {
                return false;
            }
        }

        if (Upper is not null)
        {
            var c = string.CompareOrdinal(key, Upper);
            if (c > 0 || (c == 0 && UpperExclusive))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Compares two entries in this query's order (key, then id; reversed when descending).</summary>
    public int Compare(SyncIndexCursor a, SyncIndexCursor b)
    {
        var c = string.CompareOrdinal(a.Key, b.Key);
        if (c == 0)
        {
            c = string.CompareOrdinal(a.Id, b.Id);
        }

        return IsDescending ? -c : c;
    }
}

/// <summary>A position in an index: the key and id of the last document returned.</summary>
/// <param name="Key">The encoded key.</param>
/// <param name="Id">The document id.</param>
public readonly record struct SyncIndexCursor(string Key, string Id);

/// <summary>
/// The encoding of index values into strings whose ordinal order (UTF-16 code units, as <see cref="string.CompareOrdinal(string, string)"/>
/// and IndexedDB compare) is the values' order. A one-character type tag comes first, so <see langword="null"/> sorts
/// before every value and different types never interleave. Numbers and instants become fixed-width hexadecimal; strings
/// follow their tag unchanged (ordinal, not culture-aware, order).
/// </summary>
public static class SyncIndexKey
{
    private static readonly HashSet<Type> Supported =
    [
        typeof(string), typeof(bool), typeof(char), typeof(sbyte), typeof(byte), typeof(short), typeof(ushort), typeof(int),
        typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal), typeof(DateTimeOffset),
        typeof(DateTime), typeof(DateOnly), typeof(TimeOnly), typeof(TimeSpan), typeof(Guid),
    ];

    /// <summary>Encodes a value (see the type remarks of <see cref="SyncIndex{TDocument}.Create"/> for the types).</summary>
    public static string Encode<TValue>(TValue value) => value switch
    {
        null => "0",
        bool b => b ? "11" : "10",
        string s => "s" + s,
        char c => "s" + c,
        sbyte or short or int or long => Signed("2", Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        byte or ushort or uint => Signed("2", Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        ulong u => "3" + u.ToString("x16", CultureInfo.InvariantCulture),
        float f => Real((double)f),
        double d => Real(d),
        decimal m => Real((double)m),
        DateTimeOffset o => "5" + o.UtcTicks.ToString("x16", CultureInfo.InvariantCulture),
        DateTime t => "5" + (t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t).Ticks.ToString("x16", CultureInfo.InvariantCulture),
        DateOnly d => "6" + d.DayNumber.ToString("x8", CultureInfo.InvariantCulture),
        TimeOnly t => "7" + t.Ticks.ToString("x16", CultureInfo.InvariantCulture),
        TimeSpan t => Signed("8", t.Ticks),
        Guid g => "9" + g.ToString("N"),
        Enum e => Signed("2", Convert.ToInt64(e, CultureInfo.InvariantCulture)),
        _ => throw new NotSupportedException($"Values of type {value.GetType()} cannot be indexed."),
    };

    internal static void EnsureSupported(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (!Supported.Contains(underlying) && !underlying.IsEnum)
        {
            throw new NotSupportedException($"Values of type {type} cannot be indexed.");
        }
    }

    // Flipping the sign bit makes two's-complement order unsigned order.
    private static string Signed(string tag, long value) => tag + ((ulong)value ^ 0x8000_0000_0000_0000UL).ToString("x16", CultureInfo.InvariantCulture);

    // IEEE 754 order: negative numbers have all bits inverted, positive ones the sign bit set. NaN sorts last; -0 is 0.
    private static string Real(double value)
    {
        if (double.IsNaN(value))
        {
            return "4ffffffffffffffff~";
        }

        var bits = BitConverter.DoubleToUInt64Bits(value == 0 ? 0d : value);
        var ordered = (bits & 0x8000_0000_0000_0000UL) != 0 ? ~bits : bits | 0x8000_0000_0000_0000UL;
        return "4" + ordered.ToString("x16", CultureInfo.InvariantCulture);
    }
}

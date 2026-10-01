namespace Coo.Domain.Common;

// Typed IDs (Spec 001 §3.1). The domain never generates them: callers pass a UUIDv7 (ADR 0002 §1).

public readonly record struct HouseholdId
{
    public HouseholdId(Guid value) => Value = GuardedId.NotEmpty(value);

    public Guid Value { get; }

    public override string ToString() => Value.ToString();
}

public readonly record struct BabyId
{
    public BabyId(Guid value) => Value = GuardedId.NotEmpty(value);

    public Guid Value { get; }

    public override string ToString() => Value.ToString();
}

public readonly record struct UserId
{
    public UserId(Guid value) => Value = GuardedId.NotEmpty(value);

    public Guid Value { get; }

    public override string ToString() => Value.ToString();
}

public readonly record struct DeviceId
{
    public DeviceId(Guid value) => Value = GuardedId.NotEmpty(value);

    public Guid Value { get; }

    public override string ToString() => Value.ToString();
}

public readonly record struct EntryId
{
    public EntryId(Guid value) => Value = GuardedId.NotEmpty(value);

    public Guid Value { get; }

    public override string ToString() => Value.ToString();
}

public readonly record struct DismissalId
{
    public DismissalId(Guid value) => Value = GuardedId.NotEmpty(value);

    public Guid Value { get; }

    public override string ToString() => Value.ToString();
}

internal static class GuardedId
{
    public static Guid NotEmpty(Guid value) =>
        value == Guid.Empty ? throw new ArgumentException("An ID cannot be Guid.Empty.", nameof(value)) : value;
}

/// <summary>
/// The one ordering of GUIDs used everywhere a total order is needed (HLC tie-break, canonical pairs):
/// the 16 bytes in RFC 4122 big-endian order, so the Kotlin watch app compares identically (Spec 001 §3.3).
/// </summary>
public static class GuidOrder
{
    public static int Compare(Guid a, Guid b)
    {
        Span<byte> left = stackalloc byte[16];
        Span<byte> right = stackalloc byte[16];
        a.TryWriteBytes(left, bigEndian: true, out _);
        b.TryWriteBytes(right, bigEndian: true, out _);
        return left.SequenceCompareTo(right);
    }
}

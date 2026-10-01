namespace Coo.Domain.CareLog;

/// <summary>
/// A kind's own data. Every subtype is an immutable record whose operations return a new value
/// (Spec 001 §6.2), and belongs to exactly one kind in <see cref="EntryKinds"/>.
/// </summary>
#pragma warning disable CA1012 // Abstract records need a constructor the subtypes can reach.
public abstract record EntryDetails;
#pragma warning restore CA1012

using Coo.Domain.Common;

namespace Coo.Domain.CareLog;

/// <summary>Who did something (Spec 001 §4.6).</summary>
public abstract record PersonRef
{
    private protected PersonRef()
    {
    }

    public const string Unresolved = "person.unresolved";

    /// <summary>
    /// The person holding this phone, before they have signed in (ADR 0003 §1). Rewritten to a
    /// <see cref="UserPerson"/> at first sign-in; the server rejects it.
    /// </summary>
    public static PersonRef ThisDevicesPerson { get; } = new ThisDevicesPersonRef();

    public static PersonRef User(UserId userId) => new UserPerson(userId);
}

public sealed record UserPerson(UserId UserId) : PersonRef;

public sealed record ThisDevicesPersonRef : PersonRef;

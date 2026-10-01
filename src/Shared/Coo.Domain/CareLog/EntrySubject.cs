using Coo.Domain.Common;

namespace Coo.Domain.CareLog;

/// <summary>Whom an entry is about (Spec 001 §4.4).</summary>
public abstract record EntrySubject
{
    private protected EntrySubject()
    {
    }

    public abstract SubjectType Type { get; }

    public static EntrySubject Baby(BabyId babyId) => new BabySubject(babyId);

    public static EntrySubject Caregiver(PersonRef person) => new CaregiverSubject(person);
}

public sealed record BabySubject(BabyId BabyId) : EntrySubject
{
    public override SubjectType Type => SubjectType.Baby;
}

public sealed record CaregiverSubject(PersonRef Person) : EntrySubject
{
    public override SubjectType Type => SubjectType.Caregiver;
}

public enum SubjectType
{
    Baby,
    Caregiver,
}

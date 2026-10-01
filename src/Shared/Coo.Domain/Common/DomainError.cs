namespace Coo.Domain.Common;

/// <summary>
/// A rule violation, identified by a stable dotted code (Spec 001 §3.2, §13). Codes are localized by the
/// app and returned by the server as the <c>rejected</c> reason. Once released, a code is never renamed.
/// </summary>
public sealed record DomainError(string Code, IReadOnlyDictionary<string, string>? Arguments = null)
{
    public static DomainError Of(string code) => new(code);

    public static DomainError Of(string code, string argument, string value) =>
        new(code, new Dictionary<string, string> { [argument] = value });

    public override string ToString() => Code;
}

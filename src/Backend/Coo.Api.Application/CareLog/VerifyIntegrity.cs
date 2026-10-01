namespace Coo.Api.Application.CareLog;

/// <summary>What the integrity scan found (ADR 0010 G5).</summary>
public sealed record IntegrityReport(int Checked, IReadOnlyList<UnreadableServerEntry> Failures)
{
    public bool IsClean => Failures.Count == 0;
}

/// <summary>Decodes every stored row through the codecs and the Domain rules, and reports each failure by ID.</summary>
public sealed class VerifyIntegrity(ICareEntryStore store)
{
    public async Task<IntegrityReport> ExecuteAsync(CancellationToken cancellationToken)
    {
        var checkedRows = 0;
        var failures = new List<UnreadableServerEntry>();
        await foreach (var row in store.ScanAsync(cancellationToken))
        {
            checkedRows++;
            if (row is UnreadableServerEntry unreadable)
                failures.Add(unreadable);
        }

        return new IntegrityReport(checkedRows, failures);
    }
}

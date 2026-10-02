namespace Coo.Api.IntegrationTests;

/// <summary>Spec 011 §10 (Api integration): two devices converge after interleaved offline edits, deletes and a revived edit.</summary>
public sealed class ConvergenceTests(AuthApi api) : IClassFixture<AuthApi>
{
    [Fact]
    public async Task PushAndPull_InterleavedOfflineWork_BothDevicesShowTheSameLog()
    {
        // Arrange: one account on two phones; the second joins the first's household.
        using var client = api.Factory.CreateClient();
        var household = Calls.Id(8001);
        var first = await SyncCalls.SignInWithConsentAsync(client, "converge", Calls.Id(8002), household);
        var second = await Calls.SignInAsync(client, "converge", Calls.Id(8003), Calls.Id(8004));
        var a = new SimulatedDevice(client, first.AccessToken, household, Calls.Id(8002), 1, SyncCalls.Wall);
        var b = new SimulatedDevice(client, second.AccessToken, second.HouseholdId, Calls.Id(8003), 2, SyncCalls.Wall);
        var (x, y, w, z) = (Calls.Id(8101), Calls.Id(8102), Calls.Id(8103), Calls.Id(8104));

        a.Log(x, "x");
        a.Log(y, "y");
        a.Log(w, "w");
        await a.PushAsync();
        await b.PullAsync();

        // Both offline.
        a.Wait(TimeSpan.FromSeconds(1));
        a.Edit(x, "x by A");                      // earlier than B's edit: loses
        b.Wait(TimeSpan.FromSeconds(5));
        b.Edit(x, "x by B");                      // the later correction: wins
        b.Delete(y);                              // later than A's edit of y: the delete wins
        a.Wait(TimeSpan.FromSeconds(1));
        a.Edit(y, "y by A");
        b.Wait(TimeSpan.FromSeconds(1));
        b.Delete(w);                              // a stale delete …
        a.Wait(TimeSpan.FromSeconds(10));
        a.Edit(w, "w revived by A");              // … loses to a later edit: w comes back
        a.Log(z, "z");
        a.Delete(z);                              // never on the server (Review Focus 4)

        // Act: back online, in an interleaved order. A has pushed but never pulled: its first pull starts at cursor 0
        // while it holds y, which B deleted (ruling 20).
        await b.PushAsync();
        await a.PushAsync();
        await b.PullAsync();
        await a.PullAsync();
        await b.PullAsync();

        // Assert
        var expected = new Dictionary<Guid, string?> { [x] = "x by B", [w] = "w revived by A" };
        a.Timeline().Should().BeEquivalentTo(expected);
        b.Timeline().Should().BeEquivalentTo(expected);
        a.Pending.Should().Be(0, "every change got a final result");
        b.Pending.Should().Be(0);
        b.Records.Should().NotContainKey(z, "an entry created and deleted offline never reaches another phone");
    }
}

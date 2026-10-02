using Coo.Api.Hosting;
using Microsoft.AspNetCore.Hosting;

namespace Coo.Api.IntegrationTests;

/// <summary>Spec 010 §4.3: the choice is a pure function, and Production refuses to start without the settings.</summary>
public sealed class StartupChecksTests
{
    private const string ClientId = "client.apps.googleusercontent.com";
    private static readonly byte[] DevelopmentKey = [.. Enumerable.Repeat((byte)9, 32)];
    private static readonly string KeyA = Convert.ToBase64String([.. Enumerable.Repeat((byte)1, 32)]);
    private static readonly string KeyB = Convert.ToBase64String([.. Enumerable.Repeat((byte)2, 32)]);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SelectAuth_ProductionWithoutTheClientId_ThrowsNamingTheSetting(string? clientId)
    {
        // Arrange: the dangerous case. Without an audience to check, sign-in must not start at all.
        var keys = Keys(("k1", KeyA));

        // Act
        var act = () => StartupChecks.SelectAuth(clientId, keys, null, isProduction: true, DevelopmentKey);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*Auth:GoogleWebClientId*");
    }

    [Fact]
    public void SelectAuth_ProductionWithoutKeys_ThrowsNamingTheSetting()
    {
        // Arrange: a random key per process would sign everyone out at every restart.
        var keys = Keys();

        // Act
        var act = () => StartupChecks.SelectAuth(ClientId, keys, null, isProduction: true, DevelopmentKey);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*Auth:SigningKeys*");
    }

    [Fact]
    public void SelectAuth_ProductionComplete_ReturnsTheSettings()
    {
        // Arrange
        var keys = Keys(("k1", KeyA));

        // Act
        var settings = StartupChecks.SelectAuth(ClientId, keys, null, isProduction: true, DevelopmentKey);

        // Assert
        settings.GoogleWebClientId.Should().Be(ClientId);
        settings.ActiveKeyId.Should().Be("k1");
        settings.SigningKeys["k1"].Should().Equal(Convert.FromBase64String(KeyA));
    }

    [Fact]
    public void SelectAuth_DevelopmentWithoutSettings_UsesTheDevelopmentKeyAndNoClientId()
    {
        // Arrange
        var keys = Keys();

        // Act
        var settings = StartupChecks.SelectAuth(null, keys, null, isProduction: false, DevelopmentKey);

        // Assert
        settings.GoogleWebClientId.Should().BeNull("with no audience, every sign-in is refused, never accepted");
        settings.ActiveKeyId.Should().Be(StartupChecks.DevelopmentKeyId);
        settings.SigningKeys[StartupChecks.DevelopmentKeyId].Should().Equal(DevelopmentKey);
    }

    [Theory]
    [InlineData("c2hvcnQ=")]
    [InlineData("not base64!")]
    public void SelectAuth_UnusableKey_ThrowsEvenOutsideProduction(string key)
    {
        // Arrange: a key that is set but unusable is always a mistake.
        var keys = Keys(("k1", key));

        // Act
        var act = () => StartupChecks.SelectAuth(ClientId, keys, null, isProduction: false, DevelopmentKey);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*Auth:SigningKeys:k1*openssl rand -base64 32*");
    }

    [Fact]
    public void SelectAuth_TwoKeysWithoutTheActiveOne_Throws()
    {
        // Arrange
        var keys = Keys(("k1", KeyA), ("k2", KeyB));

        // Act
        var act = () => StartupChecks.SelectAuth(ClientId, keys, null, isProduction: true, DevelopmentKey);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*Auth:ActiveSigningKeyId*");
    }

    [Fact]
    public void SelectAuth_TwoKeysWithTheActiveOne_SignsWithItAndKeepsBoth()
    {
        // Arrange: a rotation in progress (ADR 0003 §4).
        var keys = Keys(("k1", KeyA), ("k2", KeyB));

        // Act
        var settings = StartupChecks.SelectAuth(ClientId, keys, "k2", isProduction: true, DevelopmentKey);

        // Assert
        settings.ActiveKeyId.Should().Be("k2");
        settings.SigningKeys.Keys.Should().BeEquivalentTo("k1", "k2");
    }

    [Fact]
    public void SelectAuth_ActiveKeyNotConfigured_Throws()
    {
        // Arrange
        var keys = Keys(("k1", KeyA));

        // Act
        var act = () => StartupChecks.SelectAuth(ClientId, keys, "k2", isProduction: true, DevelopmentKey);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*'k2'*");
    }

    [Fact]
    public void SelectAuth_ThreeKeys_Throws()
    {
        // Arrange
        var keys = Keys(("k1", KeyA), ("k2", KeyB), ("k3", KeyA));

        // Act
        var act = () => StartupChecks.SelectAuth(ClientId, keys, "k3", isProduction: true, DevelopmentKey);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*at most two*");
    }

    [Fact]
    public async Task Start_ProductionWithoutTheClientId_FailsAtStartUp()
    {
        // Arrange: no database is needed; start-up fails before it is touched.
        await using var api = TestApi.Create("Host=unused", configure: h => h.UseSetting(StartupChecks.GoogleWebClientIdKey, ""));

        // Act
        var act = () => api.CreateClient();

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*Auth:GoogleWebClientId*");
    }

    [Fact]
    public async Task Start_ProductionWithoutKeys_FailsAtStartUp()
    {
        // Arrange
        await using var api = TestApi.Create("Host=unused", configure: h => h.UseSetting(StartupChecks.SigningKeysSection + ":k1", ""));

        // Act
        var act = () => api.CreateClient();

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*Auth:SigningKeys*");
    }

    private static List<KeyValuePair<string, string?>> Keys(params (string Id, string Value)[] keys) =>
        [.. keys.Select(k => KeyValuePair.Create(k.Id, (string?)k.Value))];
}

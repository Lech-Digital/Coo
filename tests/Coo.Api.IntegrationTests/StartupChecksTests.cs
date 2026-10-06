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

    [Fact]
    public void SelectFingerprints_ProductionWithNone_ThrowsNamingTheSetting()
    {
        // Act
        var act = () => StartupChecks.SelectFingerprints([], isProduction: true);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*AppLinks:Sha256CertFingerprints*");
    }

    [Theory]
    [InlineData("6E:7E:3C")]
    [InlineData("6E7E3C4E889535CEFE901C8CD2C537D1E42CFB914DB72DBBF57AC8BD153A287C")]
    [InlineData("GG:7E:3C:4E:88:95:35:CE:FE:90:1C:8C:D2:C5:37:D1:E4:2C:FB:91:4D:B7:2D:BB:F5:7A:C8:BD:15:3A:28:7C")]
    public void SelectFingerprints_Malformed_ThrowsInEveryEnvironment(string value)
    {
        // Act
        var act = () => StartupChecks.SelectFingerprints([value], isProduction: false);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*32 colon-separated hex bytes*");
    }

    [Fact]
    public void SelectFingerprints_LowerCaseAndBlanks_AreNormalisedAndSkipped()
    {
        // Act
        var fingerprints = StartupChecks.SelectFingerprints(
            ["6e:7e:3c:4e:88:95:35:ce:fe:90:1c:8c:d2:c5:37:d1:e4:2c:fb:91:4d:b7:2d:bb:f5:7a:c8:bd:15:3a:28:7c", " ", null], isProduction: true);

        // Assert
        fingerprints.Should().Equal(TestApi.Fingerprint);
    }

    [Fact]
    public void SelectFingerprints_DevelopmentWithNone_IsAnEmptyList()
    {
        // Act
        var fingerprints = StartupChecks.SelectFingerprints([], isProduction: false);

        // Assert
        fingerprints.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_ProductionWithoutAFingerprint_FailsAtStartUp()
    {
        // Arrange: as the two tests above, start-up fails before the database is touched.
        await using var api = TestApi.Create("Host=unused", configure: h => h.UseSetting(StartupChecks.FingerprintsSection + ":0", ""));

        // Act
        var act = () => api.CreateClient();

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*AppLinks:Sha256CertFingerprints*");
    }

    private static List<KeyValuePair<string, string?>> Keys(params (string Id, string Value)[] keys) =>
        [.. keys.Select(k => KeyValuePair.Create(k.Id, (string?)k.Value))];
}

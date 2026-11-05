using System.Net;
using System.Text.Json;

namespace Coo.Api.IntegrationTests;

/// <summary>Spec 011b §4.2–§4.3: the App Link's file and the page a browser shows.</summary>
public sealed class AppLinkTests(AuthApi api) : IClassFixture<AuthApi>
{
    [Fact]
    public async Task AssetLinks_Anonymous_ListsThePackageAndTheConfiguredFingerprint()
    {
        // Arrange
        using var client = api.Factory.CreateClient(new() { AllowAutoRedirect = false });

        // Act
        var response = await client.GetAsync(new Uri("/.well-known/assetlinks.json", UriKind.Relative));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var statement = json.RootElement.EnumerateArray().Single();
        statement.GetProperty("relation").EnumerateArray().Single().GetString().Should().Be("delegate_permission/common.handle_all_urls");
        var target = statement.GetProperty("target");
        target.GetProperty("namespace").GetString().Should().Be("android_app");
        target.GetProperty("package_name").GetString().Should().Be("com.lechdigital.coo");
        target.GetProperty("sha256_cert_fingerprints").EnumerateArray().Select(f => f.GetString()).Should().Equal(TestApi.Fingerprint);
    }

    [Theory]
    [InlineData("/i/ABCDEFGH")]
    [InlineData("/i/abcd-efgh")]
    public async Task Page_AWellFormedCode_ShowsItHyphenatedWithThePlayLinkAndTheHeaders(string path)
    {
        // Arrange
        using var client = api.Factory.CreateClient();

        // Act
        var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        html.Should().Contain("ABCD-EFGH").And.Contain("https://play.google.com/store/apps/details?id=com.lechdigital.coo")
            .And.Contain("Settings → Family → Join a family").And.Contain("https://lechdigital.nl/projects/mochi/privacy")
            .And.NotContain("<script").And.NotContain("<img");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.GetValues("Referrer-Policy").Single().Should().Be("no-referrer");
        response.Headers.GetValues("X-Robots-Tag").Single().Should().Be("noindex");
        response.Headers.GetValues("Content-Security-Policy").Single().Should().Be("default-src 'none'; style-src 'unsafe-inline'");
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Theory]
    [InlineData("/i/ABC")]
    [InlineData("/i/<script>alert(1)</script>")]
    [InlineData("/i/")]
    public async Task Page_AMalformedCode_ShowsThePageWithoutACode(string path)
    {
        // Arrange
        using var client = api.Factory.CreateClient();

        // Act
        var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("enter the code from your invitation").And.NotContain("<script>alert");
    }

    [Fact]
    public async Task Page_AnUnknownButWellFormedCode_IsNeverLookedUp()
    {
        // Arrange: no oracle (spec §4.3): a code nobody created reads exactly like one that exists.
        using var client = api.Factory.CreateClient();

        // Act
        var html = await (await client.GetAsync(new Uri("/i/ZZZZZZZZ", UriKind.Relative))).Content.ReadAsStringAsync();

        // Assert
        html.Should().Contain("ZZZZ-ZZZZ").And.NotContain("Andrzej");
    }
}

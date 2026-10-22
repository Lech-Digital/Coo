using System.Net;

namespace Coo.Api.IntegrationTests;

/// <summary>The account-deletion page Play's Data safety form links to.</summary>
public sealed class DeleteAccountPageTests(AuthApi api) : IClassFixture<AuthApi>
{
    [Fact]
    public async Task Get_DeleteAccount_ExplainsBothWaysWithoutSigningIn()
    {
        // Arrange
        using var client = api.Factory.CreateClient();

        // Act
        var response = await client.GetAsync(new Uri("/delete-account", UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Settings → Account → Delete account", html, StringComparison.Ordinal);
        Assert.Contains("mailto:andrzej@lechdigital.nl", html, StringComparison.Ordinal);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Coo.Api.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Coo.Api.IntegrationTests;

/// <summary>The API as Caddy sees it (Spec 008 §5, §9): start-up, <c>/health</c>, and the <c>verify</c> command.</summary>
public sealed class ApiTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();

    public async Task InitializeAsync()
    {
        try
        {
            await _postgres.StartAsync();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                "The PostgreSQL tests need a running Docker: start Docker Desktop (Spec 008 §9). CI has Docker.", e);
        }
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Health_DatabaseReady_Returns200WithTheVersionAndAMigratedDatabase()
    {
        // Arrange
        await using var api = Api(_postgres.GetConnectionString());
        using var client = api.CreateClient();

        // Act
        var response = await client.GetAsync(new Uri("/health", UriKind.Relative));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("ok");
        body.GetProperty("database").GetString().Should().Be("ok");
        body.GetProperty("version").GetString().Should().StartWith("0.11.0");
        (await KindVersionsAsync()).Should().BePositive("start-up migrates and registers the kinds before serving");
    }

    [Fact]
    public async Task Health_DatabaseGone_Returns503()
    {
        // Arrange
        await using var api = Api(_postgres.GetConnectionString());
        using var client = api.CreateClient();
        (await client.GetAsync(new Uri("/health", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.OK);

        // Act
        await _postgres.StopAsync();
        var response = await client.GetAsync(new Uri("/health", UriKind.Relative));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("database").GetString().Should().Be("unreachable");
    }

    [Fact]
    public async Task Start_WithoutAConnectionString_FailsAtStartUpAndNamesTheSetting()
    {
        // Arrange
        await using var api = Api(connectionString: "");

        // Act
        var act = () => api.CreateClient();

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*ConnectionStrings:Coo*");
    }

    [Fact]
    public async Task Verify_CleanDatabase_ExitsZeroAndSaysSo()
    {
        // Arrange
        await using var api = Api(_postgres.GetConnectionString());
        using var client = api.CreateClient();
        using var output = new StringWriter();

        // Act
        var exitCode = await VerifyCommand.RunAsync(api.Services, output, CancellationToken.None);

        // Assert
        exitCode.Should().Be(0);
        output.ToString().Should().Contain("0 rows checked, 0 failures");
    }

    private static WebApplicationFactory<Program> Api(string connectionString) =>
        TestApi.Create(connectionString);

    private async Task<long> KindVersionsAsync()
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM entry_kind_versions;", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}

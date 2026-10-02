using Coo.Api.Application.Auth;
using Coo.Api.Application.CareLog;
using Coo.Api.Infrastructure.Persistence;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Coo.Api.Infrastructure.Tests;

/// <summary>
/// One real PostgreSQL 17, the version production runs, shared by the whole test assembly. Each test gets its own
/// database on it (Spec 008 §9).
/// </summary>
public sealed class PostgresServer : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        try
        {
            await _container.StartAsync();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                "The PostgreSQL tests need a running Docker: start Docker Desktop (Spec 008 §9). CI has Docker.", e);
        }
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class PostgresTests : ICollectionFixture<PostgresServer>
{
    public const string Name = "PostgreSQL";
}

/// <summary>A fresh, migrated database, and the stores wired as the API wires them.</summary>
internal sealed class ServerDatabase : IAsyncDisposable
{
    public static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-00000000c0a1"));
    public static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-00000000c0b1"));
    public static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-00000000c0d1"));

    private static int _databases;
    private readonly ServiceProvider _services;
    private long _wall = 1_790_000_000_000;

    private ServerDatabase(ServiceProvider services, string connectionString, FakeTimeProvider time)
    {
        _services = services;
        ConnectionString = connectionString;
        Time = time;
    }

    public string ConnectionString { get; }

    public FakeTimeProvider Time { get; }

    public ICareEntryStore Store => _services.GetRequiredService<ICareEntryStore>();

    public IHouseholdRegistry Households => _services.GetRequiredService<IHouseholdRegistry>();

    public NpgsqlDataSource DataSource => _services.GetRequiredService<NpgsqlDataSource>();

    public IAuthNonceStore Nonces => _services.GetRequiredService<IAuthNonceStore>();

    public IDeviceSessionStore Sessions => _services.GetRequiredService<IDeviceSessionStore>();

    /// <summary>A request's worth of scoped services: <see cref="IAccountStore"/> lives here.</summary>
    public AsyncServiceScope NewScope() => _services.CreateAsyncScope();

    public static async Task<ServerDatabase> CreateAsync(PostgresServer server, bool migrate = true)
    {
        var name = "t_" + Interlocked.Increment(ref _databases).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await using (var admin = new NpgsqlConnection(server.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name};", admin);
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(server.ConnectionString) { Database = name }.ConnectionString;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(time).AddApiInfrastructure(connectionString);
        var database = new ServerDatabase(services.BuildServiceProvider(), connectionString, time);
        if (migrate)
            await database.MigrateAsync();
        return database;
    }

    public async Task MigrateAsync()
    {
        var contexts = _services.GetRequiredService<IDbContextFactory<CooDbContext>>();
        await using var context = await contexts.CreateDbContextAsync();
        await context.Database.MigrateAsync();
        await DatabaseStartup.RegisterKindsAsync(context, CancellationToken.None);
    }

    /// <summary>Migrates up to, and including, <paramref name="migration"/> only: production as it was before a change.</summary>
    public async Task MigrateToAsync(string migration)
    {
        var contexts = _services.GetRequiredService<IDbContextFactory<CooDbContext>>();
        await using var context = await contexts.CreateDbContextAsync();
        await context.Database.GetService<IMigrator>().MigrateAsync(migration);
    }

    public HlcStamp NextStamp() => new(++_wall, 0, Device);

    /// <summary>A new v7 ID from the fake clock (the real one is banned in tests too).</summary>
    public Guid NewId() => Guid.CreateVersion7(Time.GetUtcNow().AddMilliseconds(++_wall));

    public async Task<int> ExecuteAsync(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<object?> ScalarAsync(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();
}

using Coo.Mobile.Application;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>The MAUI host's composition (ruling F5): with only the platform services the host adds, sync resolves.</summary>
public sealed class CompositionTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "coo-composition-" + Path.GetRandomFileName());

    public CompositionTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not a failure.
        }
    }

    [Fact]
    public void Resolve_SyncEngineAndBringEntriesOver_NeedOnlyThePlatformServices()
    {
        // Arrange: the host registers the secret store (SecureStorage) and the clock; nothing else comes from outside.
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new ManualTimeProvider(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero)));
        services.AddSingleton<ISecretStore>(new MemorySecrets());
        services.AddMobileApplication();
        services.AddMobileInfrastructure(Path.Combine(_folder, "coo.db"), "0.11.0", new Uri("https://api.test/"));
        // ValidateOnBuild stays off: the host registers more services (pages, view models) than this test builds.
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = true });

        // Act
        var engine = provider.GetRequiredService<SyncEngine>();
        var bringOver = provider.GetRequiredService<BringEntriesOver>();
        var api = provider.GetRequiredService<ISyncApi>();
        var consent = provider.GetRequiredService<IConsentApi>();

        // Assert
        engine.Should().NotBeNull();
        bringOver.Should().NotBeNull();
        api.Should().BeSameAs(consent);
        provider.GetRequiredService<ISyncControl>().Should().BeSameAs(engine);
        engine.Dispose();
    }

    [Fact]
    public void Resolve_FamilyUseCases_NeedOnlyThePlatformServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new ManualTimeProvider(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero)));
        services.AddSingleton<ISecretStore>(new MemorySecrets());
        services.AddMobileApplication();
        services.AddMobileInfrastructure(Path.Combine(_folder, "coo.db"), "0.12.0", new Uri("https://api.test/"));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = true });

        // Act
        var families = provider.GetRequiredService<Coo.Mobile.Application.Households.Families>();
        var api = provider.GetRequiredService<Coo.Mobile.Application.Households.IHouseholdApi>();

        // Assert
        families.Should().NotBeNull();
        api.Should().NotBeNull();
    }
}

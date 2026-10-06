using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Setup;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Walk fix F5: the baby step's subtitle is true whether or not the person is signed in.</summary>
[Collection(nameof(StringsState))]
public sealed class SetupHintTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 21, 0, 0, TimeSpan.Zero);
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();

    public SetupHintTests()
    {
        Strings.Use(new LanguagePreference("en"), null);
        _account = new Account(_sessions);
    }

    public void Dispose()
    {
        _account.Dispose();
        Strings.Use(LanguagePreference.System, null);
    }

    private SetupViewModel Setup() =>
        new(_households, new SetUpBaby(_households, Substitute.For<IVersionClock>(), new FakeTimeProvider(Now)), new FakeTimeProvider(Now), _account);

    [Fact]
    public void Hint_SignedOut_SaysNoAccountIsNeeded()
    {
        // Act
        var hint = Setup().Hint;

        // Assert
        hint.Should().Be("No account needed to start. You can share with family later.");
    }

    [Fact]
    public async Task Hint_SignedIn_InvitesToStartTheFamilysLog()
    {
        // Arrange: after a removal made a new family (§8.4), or a second account's first sign-in.
        await _account.SignedInAsync(
            new StoredSession("a", Now, "r", new AccountProfile(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1")), "Kasia", "k@example.com"), Household, JoinPending: false),
            CancellationToken.None);

        // Act
        var hint = Setup().Hint;

        // Assert
        hint.Should().Be("Add your baby to start your family's log.");
    }

    [Fact]
    public async Task Hint_JoinPending_InvitesToStartTheFamilysLog()
    {
        // Arrange
        await _account.SignedInAsync(
            new StoredSession("a", Now, "r", new AccountProfile(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1")), "Kasia", "k@example.com"), Household, JoinPending: true),
            CancellationToken.None);

        // Act
        var hint = Setup().Hint;

        // Assert
        hint.Should().Be("Add your baby to start your family's log.");
    }
}

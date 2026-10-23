using Coo.Api.Application.AiParsing;
using Coo.Api.Application.Billing;
using Coo.Api.Application.Sync;
using Coo.Contracts.AiParsing;
using Coo.Contracts.Sync;
using Coo.Domain.CareLog.Time;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Microsoft.Extensions.Time.Testing;

namespace Coo.Api.Application.Tests;

public sealed class TextPseudonymiserTests
{
    private static readonly KnownName[] Names = [new("Zosia", "Baby A"), new("Andrzej", "Caregiver 1")];

    [Theory]
    [InlineData("Zosia ate 120 ml", "Baby A ate 120 ml")]
    [InlineData("nakarmiłem Zosię z lewej", "nakarmiłem Baby A z lewej")]
    [InlineData("Zosi zmieniłem pieluchę", "Baby A zmieniłem pieluchę")]
    [InlineData("ZOSIA is asleep", "Baby A is asleep")]
    [InlineData("Andrzej gave her a bottle", "Caregiver 1 gave her a bottle")]
    public void Pseudonymise_NamesInAnyCaseOrInflection_AreReplaced(string text, string expected)
    {
        // Act
        var result = TextPseudonymiser.Pseudonymise(text, Names);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void Pseudonymise_ContactAndIdData_IsMaskedButTimesAndAmountsStay()
    {
        // Arrange
        const string text = "fed 120 ml at 14:30, call me on +31 6 1234 5678 or a@b.nl, pesel 90010112345, see www.x.nl";

        // Act
        var result = TextPseudonymiser.Pseudonymise(text, []);

        // Assert
        result.Should().Be("fed 120 ml at 14:30, call me on [phone] or [email], pesel [number], see [link]");
    }

    [Fact]
    public void Pseudonymise_ShortName_DoesNotSwallowOrdinaryWords()
    {
        // Arrange: "Ania" must not stem to "An" and catch "and".
        var names = new[] { new KnownName("Ania", "Baby A") };

        // Act
        var result = TextPseudonymiser.Pseudonymise("Ania woke and ate, Anię przewinąłem", names);

        // Assert
        result.Should().Be("Baby A woke and ate, Baby A przewinąłem");
    }

    [Fact]
    public void Restore_Placeholders_GetTheNamesBack()
    {
        // Act
        var result = TextPseudonymiser.Restore("Baby A ate, Caregiver 1 fed", Names);

        // Assert
        result.Should().Be("Zosia ate, Andrzej fed");
    }
}

public sealed class ParseCareLogTextTests
{
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly UserId Parent = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 14, 0, 0, TimeSpan.FromHours(2));
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeMemberships _memberships = new();
    private readonly FakeConsents _consents = new();
    private readonly FakeAiStore _store = new();
    private readonly FakeParser _parser = new();
    private readonly FakeBilling _billing = new();

    [Fact]
    public async Task ExecuteAsync_StartLeftBreastNow_ProposesATimerAndHidesTheName()
    {
        // Arrange: the owner's example.
        await MemberAsync();
        _parser.Next = Parsed(new ParsedEntry("BreastStart", "left", null, null, null, null, null, null, TimeExpression.Now, null, "Baby A started on the left"));

        // Act
        var outcome = await Sut().ExecuteAsync(Parent, Request("Lena started drinking from left breast"), CancellationToken.None);

        // Assert
        _parser.Seen!.Text.Should().Be("Baby A started drinking from left breast");
        var proposal = outcome.Response!.Proposals.Should().ContainSingle().Subject;
        proposal.Action.Should().Be(AiActions.BreastStart);
        proposal.Side.Should().Be("left");
        proposal.At.Should().Be(Now);
        proposal.Warnings.Should().BeEmpty();
        proposal.SourceText.Should().Be("Lena started on the left");
    }

    [Fact]
    public async Task ExecuteAsync_BottleInOuncesTwentyMinutesAgo_ResolvesTimeAndMillilitres()
    {
        // Arrange
        await MemberAsync();
        _parser.Next = Parsed(new ParsedEntry("BottleFeeding", null, null, null, 4, "oz", "formula", null, TimeExpression.MinutesAgo(20), null, "4 oz"));

        // Act
        var outcome = await Sut().ExecuteAsync(Parent, Request("4 oz formula 20 minutes ago"), CancellationToken.None);

        // Assert
        var proposal = outcome.Response!.Proposals.Should().ContainSingle().Subject;
        proposal.Millilitres.Should().Be(118);
        proposal.At.Should().Be(Now.AddMinutes(-20));
        proposal.Milk.Should().Be("formula");
    }

    [Fact]
    public async Task ExecuteAsync_ImplausibleBottleAndWakeWithNothingRunning_AreFlaggedNotDropped()
    {
        // Arrange
        await MemberAsync();
        _parser.Next = Parsed(
            new ParsedEntry("BottleFeeding", null, null, null, 1200, "ml", null, null, TimeExpression.Unspecified, null, "1200"),
            new ParsedEntry("SleepEnd", null, null, null, null, null, null, null, TimeExpression.Now, null, "woke up"));

        // Act
        var outcome = await Sut().ExecuteAsync(Parent, Request("1200 ml, woke up"), CancellationToken.None);

        // Assert
        outcome.Response!.Proposals.Should().HaveCount(2);
        outcome.Response.Proposals[0].Warnings.Should().BeEquivalentTo([AiWarnings.UnusualAmount, AiWarnings.TimeNotStated]);
        outcome.Response.Proposals[1].Warnings.Should().Equal(AiWarnings.NothingRunning);
    }

    [Fact]
    public async Task ExecuteAsync_EntryMissingWhatItNeeds_GoesToUnparsed()
    {
        // Arrange
        await MemberAsync();
        _parser.Next = Parsed(new ParsedEntry("BreastStart", null, null, null, null, null, null, null, TimeExpression.Now, null, "Baby A feeding"));

        // Act
        var outcome = await Sut().ExecuteAsync(Parent, Request("Lena feeding"), CancellationToken.None);

        // Assert
        outcome.Response!.Proposals.Should().BeEmpty();
        outcome.Response.Unparsed.Should().Equal("Lena feeding");
    }

    [Fact]
    public async Task ExecuteAsync_NotAMember_IsRefusedWithoutCallingTheModel()
    {
        // Act
        var outcome = await Sut().ExecuteAsync(Parent, Request("anything"), CancellationToken.None);

        // Assert
        outcome.Refusal.Should().Be(ParseRefusal.NotAMember);
        _parser.Seen.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_FreeAllowanceUsed_SaysSoWithoutCallingTheModel()
    {
        // Arrange
        await MemberAsync();
        _store.Used = 5;

        // Act
        var outcome = await Sut().ExecuteAsync(Parent, Request("diaper"), CancellationToken.None);

        // Assert
        outcome.Response!.ErrorCode.Should().Be("ai.free-limit");
        outcome.Response.Limit.Should().Be(5);
        _parser.Seen.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ModelUnavailable_DoesNotUseAnAllowance()
    {
        // Arrange
        await MemberAsync();
        _parser.Next = null;

        // Act
        var outcome = await Sut().ExecuteAsync(Parent, Request("diaper"), CancellationToken.None);

        // Assert
        outcome.Response!.ErrorCode.Should().Be("ai.unavailable");
        _store.Counted.Should().Be(0);
    }

    private async Task MemberAsync()
    {
        _memberships.Rows[(Household, Parent)] = new MembershipLookup(Membership.Create(Household, Parent, Role.Owner, Now), IsRevoked: false);
        await new RecordConsent(_consents, _time).ExecuteAsync(Parent, CareDataConsent.Purpose, CareDataConsent.CurrentWording, "1.0.0", "en", CancellationToken.None);
    }

    private ParseCareLogText Sut() =>
        new(new SyncAccess(_memberships, _consents), _billing, _store, _parser, new AiParsingLimitsOptions(), _time);

    private static AiParseRequest Request(string text) =>
        new(Household.Value, Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"), text, Now, "Europe/Amsterdam", "en", new AiParseContext([], null, "ml"));

    private static ParsedText Parsed(params ParsedEntry[] entries) => new(entries, [], "v1");

    private sealed class FakeParser : ICareLogTextParser
    {
        public ParsedText? Next { get; set; }

        public ParsePrompt? Seen { get; private set; }

        public Task<ParsedText?> ParseAsync(ParsePrompt prompt, CancellationToken cancellationToken)
        {
            Seen = prompt;
            return Task.FromResult(Next);
        }
    }

    private sealed class FakeAiStore : IAiParsingStore
    {
        public int Used { get; set; }

        public int Counted { get; private set; }

        public Task<(IReadOnlyList<string> Babies, IReadOnlyList<string> Caregivers)> NamesAsync(HouseholdId household, CancellationToken cancellationToken) =>
            Task.FromResult<(IReadOnlyList<string>, IReadOnlyList<string>)>((["Lena"], ["Andrzej Lech"]));

        public Task<int> CountAsync(HouseholdId household, DateOnly day, CancellationToken cancellationToken) => Task.FromResult(++Counted + Used);

        public Task<int> UsedAsync(HouseholdId household, DateOnly day, CancellationToken cancellationToken) => Task.FromResult(Used);
    }

    private sealed class FakeBilling : IBillingStore
    {
        public Task SaveSubscriptionAsync(Subscription subscription, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<Subscription?> FindSubscriptionAsync(string purchaseToken, CancellationToken cancellationToken) => Task.FromResult<Subscription?>(null);

        public Task<bool> IsHouseholdPremiumAsync(HouseholdId householdId, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}

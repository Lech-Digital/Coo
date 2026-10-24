using Coo.Contracts.AiParsing;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.AiLogging;
using Coo.Mobile.Application.Households;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Coo.Mobile.Application.Tests;

public sealed class TranscribeVoiceTests
{
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly VoiceRecording Recording = new([1, 2, 3, 4], "audio/aac", TimeSpan.FromSeconds(4));
    private readonly IAiParseApi _api = Substitute.For<IAiParseApi>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();

    public TranscribeVoiceTests() =>
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns(
            [HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, new DeviceId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1")))).Value]);

    [Fact]
    public async Task ExecuteAsync_Recording_SendsItWithTheHintsAndReturnsTheWords()
    {
        // Arrange
        AiTranscribeRequest? sent = null;
        _api.TranscribeAsync(Arg.Do<AiTranscribeRequest>(r => sent = r), Arg.Any<CancellationToken>()).Returns(new AiTranscribeResponse("Lena started on the left"));

        // Act
        var voice = await new TranscribeVoice(_households, _api).ExecuteAsync(Recording, TranscribeVoice.ForLog, "pl", ["Lena"], CancellationToken.None);

        // Assert
        voice.Should().Be(new VoiceText("Lena started on the left"));
        sent!.AudioBase64.Should().Be(Convert.ToBase64String([1, 2, 3, 4]));
        sent.MimeType.Should().Be("audio/aac");
        (sent.Language, sent.Purpose, sent.TimeZoneId).Should().Be(("pl", "log", "Europe/Amsterdam"));
        sent.Hints.Should().Equal("Lena");
    }

    [Fact]
    public async Task ExecuteAsync_ServerUnreachable_IsUnavailable()
    {
        // Arrange
        _api.TranscribeAsync(Arg.Any<AiTranscribeRequest>(), Arg.Any<CancellationToken>()).Returns((AiTranscribeResponse?)null);

        // Act
        var voice = await new TranscribeVoice(_households, _api).ExecuteAsync(Recording, TranscribeVoice.ForAdvisor, "en", [], CancellationToken.None);

        // Assert
        voice.ErrorCode.Should().Be("ai.unavailable");
    }

    [Fact]
    public async Task ExecuteAsync_VoiceAllowanceUsedUp_PassesTheLimitOn()
    {
        // Arrange
        _api.TranscribeAsync(Arg.Any<AiTranscribeRequest>(), Arg.Any<CancellationToken>()).Returns(new AiTranscribeResponse("", "ai.voice-free-limit", 5));

        // Act
        var voice = await new TranscribeVoice(_households, _api).ExecuteAsync(Recording, TranscribeVoice.ForLog, "en", [], CancellationToken.None);

        // Assert
        voice.Should().Be(new VoiceText("", "ai.voice-free-limit", 5));
    }
}

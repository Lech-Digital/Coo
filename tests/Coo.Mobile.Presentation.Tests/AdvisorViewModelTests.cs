using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Advisor;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Presentation.Advisor;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

public sealed class AdvisorViewModelTests
{
    private sealed class FakeAdvisorApi : IAdvisorApi
    {
        public TaskCompletionSource<AskAdvisorResponse>? Tcs { get; set; }
        public string ResponseToReturn { get; set; } = "Here is the guidance for your baby.";
        public int CallCount { get; private set; }

        public string? LastQuestion { get; private set; }

        public async Task<AskAdvisorResponse> AskAsync(AskAdvisorRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastQuestion = request.Question;
            if (Tcs != null)
            {
                cancellationToken.Register(() => Tcs.TrySetCanceled(cancellationToken));
                return await Tcs.Task;
            }
            return new AskAdvisorResponse { Answer = ResponseToReturn };
        }

        public Task<GenerateAvatarResponse> GenerateAvatarAsync(GenerateAvatarRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new GenerateAvatarResponse { ImageBase64 = request.ImageBase64 });
        }
    }

    private sealed class InMemoryThreadStore : IAdvisorThreadStore
    {
        public List<AdvisorThread> StoredThreads { get; } = [];

        public Task<IReadOnlyList<AdvisorThreadSummary>> ListThreadsAsync(CancellationToken cancellationToken)
        {
            var summaries = StoredThreads
                .OrderByDescending(t => t.LastUpdatedAt)
                .Select(t => new AdvisorThreadSummary
                {
                    Id = t.Id,
                    Title = t.Title,
                    CreatedAt = t.CreatedAt,
                    LastUpdatedAt = t.LastUpdatedAt,
                    MessageCount = t.Messages.Count
                })
                .ToList();
            return Task.FromResult<IReadOnlyList<AdvisorThreadSummary>>(summaries);
        }

        public Task<AdvisorThread?> GetThreadAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(StoredThreads.FirstOrDefault(t => t.Id == id));

        public Task SaveThreadAsync(AdvisorThread thread, CancellationToken cancellationToken)
        {
            var idx = StoredThreads.FindIndex(t => t.Id == thread.Id);
            if (idx >= 0) StoredThreads[idx] = thread;
            else StoredThreads.Add(thread);
            return Task.CompletedTask;
        }

        public Task DeleteThreadAsync(string id, CancellationToken cancellationToken)
        {
            StoredThreads.RemoveAll(t => t.Id == id);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHouseholdStore(BabyProfile? baby) : IHouseholdStore
    {
        public Task<IReadOnlyList<HouseholdSettings>> ListHouseholdsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HouseholdSettings>>([]);

        public Task SaveSettingsAsync(HouseholdSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<BabyProfile>> ListBabiesAsync(HouseholdId householdId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BabyProfile>>(baby != null ? [baby] : []);

        public Task SaveBabyAsync(BabyProfile baby, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static (AdvisorViewModel vm, FakeAdvisorApi api, InMemoryThreadStore store) CreateSut(
        DateOnly? babyBirthDate = null, Coo.Mobile.Application.AiLogging.IVoiceRecorder? recorder = null, Coo.Mobile.Application.AiLogging.TranscribeVoice? transcribe = null)
    {
        var fixedTime = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTime(fixedTime);
        var api = new FakeAdvisorApi();
        var store = new InMemoryThreadStore();
        var sessionStore = new MemorySessions();

        var householdId = new HouseholdId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var userId = new UserId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        sessionStore.SaveAsync(new StoredSession("token", DateTimeOffset.MaxValue, "refresh", new AccountProfile(userId, "Parent", "parent@example.com"), householdId, false), CancellationToken.None);

        BabyProfile? baby = null;
        if (babyBirthDate != null)
        {
            var today = DateOnly.FromDateTime(fixedTime.DateTime);
            baby = BabyProfile.Create(
                new BabyId(Guid.Parse("33333333-3333-3333-3333-333333333333")),
                householdId,
                "Leo",
                [],
                babyBirthDate.Value,
                null,
                new Coo.Domain.Common.Versioning.HlcStamp(1, 0, new DeviceId(Guid.Parse("44444444-4444-4444-4444-444444444444"))),
                today).Value;
        }

        var householdStore = new FakeHouseholdStore(baby);
        var vm = new AdvisorViewModel(api, store, sessionStore, householdStore, timeProvider, recorder: recorder, transcribe: transcribe);
        return (vm, api, store);
    }

    private sealed class FakeRecorder(Coo.Mobile.Application.AiLogging.VoiceRecording? recording) : Coo.Mobile.Application.AiLogging.IVoiceRecorder
    {
        public bool IsAvailable => true;

        public Task<Coo.Mobile.Application.AiLogging.VoiceRecording?> RecordAsync(CancellationToken cancellationToken) => Task.FromResult(recording);

        public void Finish()
        {
        }
    }

    private static Coo.Mobile.Application.AiLogging.TranscribeVoice Transcriber(string said)
    {
        var households = NSubstitute.Substitute.For<IHouseholdStore>();
        households.ListHouseholdsAsync(NSubstitute.Arg.Any<CancellationToken>()).Returns(
            [HouseholdSettings.Create(new HouseholdId(Guid.Parse("11111111-1111-1111-1111-111111111111")), VolumeUnit.Millilitres, "Europe/Amsterdam",
                new Coo.Domain.Common.Versioning.HlcStamp(1, 0, new DeviceId(Guid.Parse("44444444-4444-4444-4444-444444444444")))).Value]);
        var api = NSubstitute.Substitute.For<Coo.Mobile.Application.AiLogging.IAiParseApi>();
        api.TranscribeAsync(NSubstitute.Arg.Any<Coo.Contracts.AiParsing.AiTranscribeRequest>(), NSubstitute.Arg.Any<CancellationToken>())
            .Returns(new Coo.Contracts.AiParsing.AiTranscribeResponse(said));
        return new Coo.Mobile.Application.AiLogging.TranscribeVoice(households, api);
    }

    [Fact]
    public async Task Speak_SpokenQuestion_IsAskedLikeATypedOne()
    {
        // Arrange
        var recording = new Coo.Mobile.Application.AiLogging.VoiceRecording(new byte[300], "audio/aac", TimeSpan.FromSeconds(3));
        var (vm, api, _) = CreateSut(new DateOnly(2026, 8, 9), new FakeRecorder(recording), Transcriber("Why does Leo wake at 4?"));
        await vm.InitializeAsync();

        // Act
        await vm.SpeakCommand.ExecuteAsync(null);

        // Assert
        api.LastQuestion.Should().Be("Why does Leo wake at 4?");
        vm.VoiceMessage.Should().BeEmpty();
        vm.IsRecording.Should().BeFalse();
    }

    [Fact]
    public async Task Speak_NothingRecorded_SaysSoAndAsksNothing()
    {
        // Arrange
        var (vm, api, _) = CreateSut(new DateOnly(2026, 8, 9), new FakeRecorder(null), Transcriber("unused"));
        await vm.InitializeAsync();

        // Act
        await vm.SpeakCommand.ExecuteAsync(null);

        // Assert
        api.CallCount.Should().Be(0);
        vm.VoiceMessage.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Initialize_WithTwoMonthOldBaby_LoadsSocialSmileStageAndQuestions()
    {
        // 60 days old -> 6-12 Weeks bracket
        var birthDate = new DateOnly(2026, 8, 9);
        var (vm, _, _) = CreateSut(birthDate);

        await vm.InitializeAsync();

        vm.DevelopmentTitle.Should().Contain("6–12 Weeks");
        vm.DevelopmentSummary.Should().NotBeNullOrWhiteSpace();
        vm.DevelopmentFullGuide.Should().Contain("Movement & Physical Skills");
        vm.SuggestedQuestions.Should().HaveCount(5);
    }

    [Fact]
    public async Task ShuffleQuestions_RotatesQuestionBatch()
    {
        var birthDate = new DateOnly(2026, 8, 9);
        var (vm, _, _) = CreateSut(birthDate);
        await vm.InitializeAsync();

        var firstBatch = vm.SuggestedQuestions.ToList();

        vm.ShuffleQuestionsCommand.Execute(null);
        var secondBatch = vm.SuggestedQuestions.ToList();

        secondBatch.Should().NotEqual(firstBatch);
    }

    [Fact]
    public async Task ToggleDevelopmentExpanded_FlipsState()
    {
        var (vm, _, _) = CreateSut();
        await vm.InitializeAsync();

        vm.IsDevelopmentExpanded.Should().BeFalse();
        vm.ToggleDevelopmentExpandedCommand.Execute(null);
        vm.IsDevelopmentExpanded.Should().BeTrue();
        vm.ToggleDevelopmentExpandedCommand.Execute(null);
        vm.IsDevelopmentExpanded.Should().BeFalse();
    }

    [Fact]
    public async Task AskAsync_CreatesThread_AndAppendsMessages()
    {
        var (vm, api, store) = CreateSut(new DateOnly(2026, 9, 1));
        await vm.InitializeAsync();

        vm.Question = "Is 4 hours between feeds okay?";
        await vm.AskCommand.ExecuteAsync(null);

        vm.Messages.Should().HaveCount(2);
        vm.Messages[0].IsUser.Should().BeTrue();
        vm.Messages[0].Text.Should().Be("Is 4 hours between feeds okay?");
        vm.Messages[1].IsUser.Should().BeFalse();
        vm.Messages[1].Text.Should().Be(api.ResponseToReturn);

        vm.HasStartedChat.Should().BeTrue();
        vm.CurrentThreadId.Should().NotBeNull();
        store.StoredThreads.Should().HaveCount(1);
        store.StoredThreads[0].Messages.Should().HaveCount(2);
    }

    [Fact]
    public async Task ResetChat_WhileQueryIsLoading_DoesNotLeakOldAnswerIntoNewChat()
    {
        var (vm, api, _) = CreateSut(new DateOnly(2026, 9, 1));
        await vm.InitializeAsync();

        var tcs = new TaskCompletionSource<AskAdvisorResponse>();
        api.Tcs = tcs;

        vm.Question = "Will my baby sleep tonight?";
        var askTask = vm.AskCommand.ExecuteAsync(null);

        vm.IsBusy.Should().BeTrue();
        vm.Messages.Should().HaveCount(1);

        // User clicks 'New' while still loading!
        vm.ResetChatCommand.Execute(null);

        vm.IsBusy.Should().BeFalse();
        vm.Messages.Should().BeEmpty();
        vm.HasStartedChat.Should().BeFalse();

        // Previous network request completes
        tcs.TrySetResult(new AskAdvisorResponse { Answer = "Old delayed answer" });
        await askTask;

        // Active messages list must still be clean and empty!
        vm.Messages.Should().BeEmpty();
        vm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task OpenThread_LoadsMessagesAndSetsCurrentThread()
    {
        var (vm, _, store) = CreateSut(new DateOnly(2026, 9, 1));
        await vm.InitializeAsync();

        var fixedNow = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var thread = new AdvisorThread
        {
            Id = "thread-123",
            Title = "Teething remedies",
            CreatedAt = fixedNow,
            LastUpdatedAt = fixedNow,
            Messages =
            [
                new AdvisorMessageItem { Id = "m1", Text = "Teething tips?", IsUser = true, Timestamp = fixedNow },
                new AdvisorMessageItem { Id = "m2", Text = "Use chilled teething rings.", IsUser = false, Timestamp = fixedNow }
            ]
        };
        store.StoredThreads.Add(thread);
        await vm.ToggleHistoryCommand.ExecuteAsync(null);

        var summary = vm.Threads.First(t => t.Id == "thread-123");
        await vm.OpenThreadCommand.ExecuteAsync(summary);

        vm.CurrentThreadId.Should().Be("thread-123");
        vm.CurrentThreadTitle.Should().Be("Teething remedies");
        vm.Messages.Should().HaveCount(2);
        vm.HasStartedChat.Should().BeTrue();
        vm.IsHistoryOpen.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteThread_DeletesFromStoreAndResetsIfActive()
    {
        var (vm, _, store) = CreateSut(new DateOnly(2026, 9, 1));
        await vm.InitializeAsync();

        var fixedNow = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var thread = new AdvisorThread
        {
            Id = "thread-del",
            Title = "To delete",
            CreatedAt = fixedNow,
            LastUpdatedAt = fixedNow,
            Messages = []
        };
        store.StoredThreads.Add(thread);
        await vm.ToggleHistoryCommand.ExecuteAsync(null);

        var summary = vm.Threads.First(t => t.Id == "thread-del");
        await vm.OpenThreadCommand.ExecuteAsync(summary);
        vm.CurrentThreadId.Should().Be("thread-del");

        await vm.DeleteThreadCommand.ExecuteAsync(summary);

        store.StoredThreads.Should().BeEmpty();
        vm.Threads.Should().BeEmpty();
        vm.CurrentThreadId.Should().BeNull();
        vm.Messages.Should().BeEmpty();
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Mobile.Application.Advisor;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Advisor;

public partial class AdvisorViewModel(
    IAdvisorApi api,
    IAdvisorThreadStore threadStore,
    ISessionStore sessionStore,
    IHouseholdStore householdStore,
    TimeProvider timeProvider,
    BuildAdvisorContext? buildContext = null) : ObservableObject, IDisposable
{
    private CancellationTokenSource? _cts;
    private string? _activeRequestId;
    private string[] _allQuestionsPool = [];
    private int _poolRotationIndex;

    [ObservableProperty]
    private string _question = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public ObservableCollection<AdvisorMessageItem> Messages { get; } = new();

    public ObservableCollection<AdvisorThreadSummary> Threads { get; } = new();

    public ObservableCollection<string> SuggestedQuestions { get; } = new();

    [ObservableProperty]
    private string? _currentThreadId;

    [ObservableProperty]
    private string _currentThreadTitle = Strings.Get("Advisor.NewConversation");

    [ObservableProperty]
    private bool _isHistoryOpen;

    [ObservableProperty]
    private string _developmentTitle = Strings.Get("Advisor.GuideTitle");

    [ObservableProperty]
    private string _developmentSummary = "• Smiling & cooing\n• Tracking objects with eyes\n• Holding head up during tummy time";

    [ObservableProperty]
    private string _developmentFullGuide = string.Empty;

    [ObservableProperty]
    private bool _isDevelopmentExpanded;

    [ObservableProperty]
    private bool _hasStartedChat;

    public bool ShowSuggestedQuestions => Messages.Count == 0 && !IsHistoryOpen;

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task InitializeAsync()
    {
        await RefreshThreadsAsync();

        var household = (await householdStore.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
        var householdId = household?.Id;
        if (householdId == null)
        {
            var session = await sessionStore.GetAsync(CancellationToken.None);
            householdId = session?.AccountHousehold;
        }

        if (householdId != null)
        {
            var babies = await householdStore.ListBabiesAsync(householdId.Value, CancellationToken.None);
            var baby = babies.FirstOrDefault(b => !b.IsDeleted);
            if (baby?.BirthDate is { } birthDate)
            {
                var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
                var daysOld = Math.Max(0, today.DayNumber - birthDate.DayNumber);

                var stage = AdvisorKnowledgeBase.GetStage(daysOld);
                DevelopmentTitle = stage.StageBadge;
                DevelopmentSummary = stage.Summary;
                DevelopmentFullGuide =
                    $"🏃 Movement & Physical Skills\n{stage.MotorSkills}\n\n" +
                    $"🧠 Cognitive & Senses\n{stage.CognitiveSensory}\n\n" +
                    $"💬 Communication & Social\n{stage.SocialSpeech}\n\n" +
                    $"💤 Rhythm, Sleep & Feeding\n{stage.RhythmSleep}\n\n" +
                    $"💡 Pediatric Tip\n{stage.PediatricTip}";

                _allQuestionsPool = AdvisorKnowledgeBase.GetQuestionPool(daysOld);
                LoadQuestionBatch();
                return;
            }
        }

        // Fallback if no baby or birth date is configured
        DevelopmentTitle = Strings.Get("Advisor.GuideTitle");
        DevelopmentSummary = Strings.Get("Advisor.GuideSummary");
        DevelopmentFullGuide = "";
        _allQuestionsPool = AdvisorKnowledgeBase.GetFallbackQuestions();
        LoadQuestionBatch();
    }

    private void LoadQuestionBatch()
    {
        SuggestedQuestions.Clear();
        if (_allQuestionsPool.Length == 0) return;

        var count = Math.Min(5, _allQuestionsPool.Length);
        for (var i = 0; i < count; i++)
        {
            var idx = (_poolRotationIndex + i) % _allQuestionsPool.Length;
            SuggestedQuestions.Add(_allQuestionsPool[idx]);
        }
    }

    [RelayCommand]
    private void ShuffleQuestions()
    {
        if (_allQuestionsPool.Length > 0)
        {
            _poolRotationIndex = (_poolRotationIndex + 4) % _allQuestionsPool.Length;
            LoadQuestionBatch();
        }
    }

    [RelayCommand]
    private void ToggleDevelopmentExpanded()
    {
        IsDevelopmentExpanded = !IsDevelopmentExpanded;
    }

    [RelayCommand]
    private async Task ToggleHistoryAsync()
    {
        IsHistoryOpen = !IsHistoryOpen;
        if (IsHistoryOpen)
        {
            await RefreshThreadsAsync();
        }
    }

    private async Task RefreshThreadsAsync()
    {
        try
        {
            var list = await threadStore.ListThreadsAsync(CancellationToken.None);
            Threads.Clear();
            foreach (var t in list)
            {
                Threads.Add(t);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load threads: {ex}");
        }
    }

    [RelayCommand]
    private async Task OpenThreadAsync(AdvisorThreadSummary? summary)
    {
        if (summary is null) return;

        // Abort any active in-flight query
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _activeRequestId = null;
        IsBusy = false;

        var fullThread = await threadStore.GetThreadAsync(summary.Id, CancellationToken.None);
        if (fullThread != null)
        {
            CurrentThreadId = fullThread.Id;
            CurrentThreadTitle = fullThread.Title;
            Messages.Clear();
            foreach (var msg in fullThread.Messages)
            {
                Messages.Add(msg);
            }
            HasStartedChat = Messages.Count > 0;
        }

        IsHistoryOpen = false;
    }

    [RelayCommand]
    private async Task DeleteThreadAsync(AdvisorThreadSummary? summary)
    {
        if (summary is null) return;

        await threadStore.DeleteThreadAsync(summary.Id, CancellationToken.None);
        Threads.Remove(summary);

        if (CurrentThreadId == summary.Id)
        {
            ResetChat();
        }
    }

    [RelayCommand]
    private void ResetChat()
    {
        // Instantly cancel any ongoing API call
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _activeRequestId = null;

        IsBusy = false;
        CurrentThreadId = null;
        CurrentThreadTitle = Strings.Get("Advisor.NewConversation");
        Messages.Clear();
        HasStartedChat = false;
        IsHistoryOpen = false;
        Question = string.Empty;
    }

    /// <summary>
    /// Asks, once, for consent to send the question and a log summary to Google's Gemini (GDPR Art. 9(2)(a): health
    /// data about a child, to a third party). Set by the page; true when given.
    /// </summary>
    public Func<Task<bool>>? EnsureConsentAsync { get; set; }

    [RelayCommand]
    private async Task AskAsync()
    {
        if (string.IsNullOrWhiteSpace(Question) || IsBusy) return;
        if (EnsureConsentAsync is { } consent && !await consent())
            return;

        var text = Question.Trim();
        Question = string.Empty;

        // Immediately abort any previous request
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var requestId = Guid.CreateVersion7(timeProvider.GetUtcNow()).ToString("N");
        _activeRequestId = requestId;

        var now = timeProvider.GetUtcNow();
        var userMsg = new AdvisorMessageItem
        {
            Id = Guid.CreateVersion7(now).ToString("N"),
            Text = text,
            IsUser = true,
            Timestamp = now
        };

        // Determine thread
        var threadId = CurrentThreadId;
        var isNewThread = false;
        if (string.IsNullOrWhiteSpace(threadId))
        {
            threadId = Guid.CreateVersion7(now).ToString("N");
            CurrentThreadId = threadId;
            var title = text.Length > 36 ? text[..36].Trim() + "…" : text;
            CurrentThreadTitle = title;
            isNewThread = true;
        }

        Messages.Add(userMsg);
        HasStartedChat = true;
        IsBusy = true;

        // Persist user message to thread immediately
        var currentThread = !isNewThread ? await threadStore.GetThreadAsync(threadId, CancellationToken.None) : null;
        currentThread ??= new AdvisorThread
        {
            Id = threadId,
            Title = CurrentThreadTitle,
            CreatedAt = now,
            LastUpdatedAt = now,
            Messages = []
        };
        currentThread.Messages.Add(userMsg);
        currentThread.LastUpdatedAt = now;
        await threadStore.SaveThreadAsync(currentThread, CancellationToken.None);
        await RefreshThreadsAsync();

        try
        {
            var session = await sessionStore.GetAsync(token);
            // The family on screen, as on Today and Log: a member of someone else's family asks about that baby
            // (owner's finding: it asked about his own empty family and was told nothing had been logged).
            var household = (await householdStore.ListHouseholdsAsync(token)).FirstOrDefault(h => !h.IsDeleted);
            var householdId = household?.Id ?? session?.AccountHousehold;
            var baby = householdId is { } h ? (await householdStore.ListBabiesAsync(h, token)).FirstOrDefault(b => !b.IsDeleted) : null;
            if (session == null || householdId is null || baby is null)
            {
                if (_activeRequestId == requestId && CurrentThreadId == threadId && !token.IsCancellationRequested)
                    Messages.Add(new AdvisorMessageItem { Text = Strings.Get("Advisor.SignIn"), IsUser = false, IsError = true, Timestamp = timeProvider.GetUtcNow() });
                return;
            }

            var history = Messages.Take(Messages.Count - 1)
                .Where(m => !m.IsError)
                .Select(m => new AdvisorMessageHistory { Text = m.Text, IsUser = m.IsUser })
                .ToList();

            var zone = household is null ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(household.TimeZoneId);
            var request = new AskAdvisorRequest
            {
                HouseholdId = householdId.Value.Value,
                BabyId = baby.Id.Value,
                Question = text,
                TimeZoneId = zone.Id,
                History = history,
                Language = Strings.Language,
                Context = buildContext is null ? null : await buildContext.ExecuteAsync(householdId.Value, baby.Id, zone, timeProvider.GetUtcNow(), token),
            };

            var response = await api.AskAsync(request, token);

            // Race-condition check: if the user clicked New or switched threads while loading, do NOT append to active screen!
            if (token.IsCancellationRequested || _activeRequestId != requestId)
            {
                // Optionally save response to background thread
                if (response?.Answer != null && response.ErrorCode is null)
                {
                    currentThread.Messages.Add(new AdvisorMessageItem { Text = response.Answer, IsUser = false, Timestamp = timeProvider.GetUtcNow() });
                    currentThread.LastUpdatedAt = timeProvider.GetUtcNow();
                    await threadStore.SaveThreadAsync(currentThread, CancellationToken.None);
                }
                return;
            }

            if (CurrentThreadId == threadId)
            {
                var failed = response?.ErrorCode is not null || string.IsNullOrWhiteSpace(response?.Answer);
                var answerText = failed ? ErrorText(response?.ErrorCode, response?.Limit) : response!.Answer!;
                var nowMsg = timeProvider.GetUtcNow();
                var assistantMsg = new AdvisorMessageItem
                {
                    Id = Guid.CreateVersion7(nowMsg).ToString("N"),
                    Text = answerText,
                    IsUser = false,
                    IsError = failed,
                    Timestamp = nowMsg
                };
                Messages.Add(assistantMsg);

                currentThread.Messages.Add(assistantMsg);
                currentThread.LastUpdatedAt = timeProvider.GetUtcNow();
                await threadStore.SaveThreadAsync(currentThread, CancellationToken.None);
                await RefreshThreadsAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation: do nothing
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && _activeRequestId == requestId && CurrentThreadId == threadId)
            {
                System.Diagnostics.Debug.WriteLine($"Advisor request failed: {ex}");
                Messages.Add(new AdvisorMessageItem
                {
                    Text = Strings.Get("Advisor.Error.offline"),
                    IsUser = false,
                    IsError = true,
                    Timestamp = timeProvider.GetUtcNow()
                });
            }
        }
        finally
        {
            if (_activeRequestId == requestId)
            {
                IsBusy = false;
                _activeRequestId = null;
            }
        }
    }

    internal static string ErrorText(string? code, int? limit) => code switch
    {
        "advisor.daily-limit" => Strings.Format("Advisor.Error.daily-limit", limit ?? 100),
        "advisor.free-limit" => Strings.Format("Advisor.Error.free-limit", limit ?? 3),
        _ => Strings.Get("Advisor.Error.unavailable"),
    };
}

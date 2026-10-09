using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Mobile.Application.Advisor;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Presentation.Advisor;

public partial class AdvisorViewModel(
    IAdvisorApi api,
    IAdvisorThreadStore threadStore,
    ISessionStore sessionStore,
    IHouseholdStore householdStore,
    TimeProvider timeProvider) : ObservableObject, IDisposable
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
    private string _currentThreadTitle = "New Conversation";

    [ObservableProperty]
    private bool _isHistoryOpen;

    [ObservableProperty]
    private string _developmentTitle = "Typical Development";

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

        var session = await sessionStore.GetAsync(CancellationToken.None);
        if (session != null)
        {
            var babies = await householdStore.ListBabiesAsync(session.AccountHousehold, CancellationToken.None);
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
        DevelopmentTitle = "Development & Milestone Guide";
        DevelopmentSummary = "Track your baby's physical, sensory, and social milestones and ask any pediatric questions.";
        DevelopmentFullGuide = "Explore age-appropriate insights on feeding, safe sleep rhythms, tummy time exercises, and growth milestones.";
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
        CurrentThreadTitle = "New Conversation";
        Messages.Clear();
        HasStartedChat = false;
        IsHistoryOpen = false;
        Question = string.Empty;
    }

    [RelayCommand]
    private async Task AskAsync()
    {
        if (string.IsNullOrWhiteSpace(Question) || IsBusy) return;

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
            if (session == null)
            {
                if (_activeRequestId == requestId && CurrentThreadId == threadId && !token.IsCancellationRequested)
                {
                    Messages.Add(new AdvisorMessageItem { Text = "You must be signed in to ask questions.", IsUser = false, Timestamp = timeProvider.GetUtcNow() });
                }
                return;
            }

            var babies = await householdStore.ListBabiesAsync(session.AccountHousehold, token);
            var babyId = babies.FirstOrDefault(b => !b.IsDeleted)?.Id.Value ?? session.AccountHousehold.Value;

            var history = Messages.Take(Messages.Count - 1)
                .Where(m => !m.Text.StartsWith("Error ", StringComparison.Ordinal))
                .Select(m => new AdvisorMessageHistory { Text = m.Text, IsUser = m.IsUser })
                .ToList();

            var request = new AskAdvisorRequest
            {
                HouseholdId = session.AccountHousehold.Value,
                BabyId = babyId,
                Question = text,
                TimeZoneId = TimeZoneInfo.Local.Id,
                History = history
            };

            var response = await api.AskAsync(request, token);

            // Race-condition check: if the user clicked New or switched threads while loading, do NOT append to active screen!
            if (token.IsCancellationRequested || _activeRequestId != requestId)
            {
                // Optionally save response to background thread
                if (response?.Answer != null)
                {
                    currentThread.Messages.Add(new AdvisorMessageItem { Text = response.Answer, IsUser = false, Timestamp = timeProvider.GetUtcNow() });
                    currentThread.LastUpdatedAt = timeProvider.GetUtcNow();
                    await threadStore.SaveThreadAsync(currentThread, CancellationToken.None);
                }
                return;
            }

            if (CurrentThreadId == threadId)
            {
                var answerText = response?.Answer ?? "Sorry, no answer could be retrieved.";
                var nowMsg = timeProvider.GetUtcNow();
                var assistantMsg = new AdvisorMessageItem
                {
                    Id = Guid.CreateVersion7(nowMsg).ToString("N"),
                    Text = answerText,
                    IsUser = false,
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
                Messages.Add(new AdvisorMessageItem
                {
                    Text = $"Error connecting to Advisor API: {ex.Message}",
                    IsUser = false,
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
}

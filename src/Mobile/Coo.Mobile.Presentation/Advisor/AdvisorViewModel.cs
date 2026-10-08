using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Advisor;

using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Presentation.Advisor;

public partial class AdvisorViewModel(IAdvisorApi api, ISessionStore sessionStore, IHouseholdStore householdStore, TimeProvider timeProvider) : ObservableObject, IDisposable
{
    [ObservableProperty]
    private string _question = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public ObservableCollection<AdvisorMessage> Messages { get; } = new();

    public ObservableCollection<string> SuggestedQuestions { get; } = new();

    [ObservableProperty]
    private string _developmentTitle = "Typical Development";

    [ObservableProperty]
    private string _developmentText = "• Smiling & cooing\n• Tracking objects with eyes\n• Holding head up during tummy time";

    public bool ShowSuggestedQuestions => Messages.Count == 0;

    [ObservableProperty]
    private bool _hasStartedChat;
    
    public async Task InitializeAsync()
    {
        SuggestedQuestions.Clear();
        var session = await sessionStore.GetAsync(CancellationToken.None);
        if (session != null)
        {
            var babies = await householdStore.ListBabiesAsync(session.AccountHousehold, CancellationToken.None);
            var baby = babies.FirstOrDefault(b => !b.IsDeleted);
            if (baby?.BirthDate is { } birthDate)
            {
                var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
                var daysOld = today.DayNumber - birthDate.DayNumber;
                
                string[] pool;
                if (daysOld < 14)
                {
                    DevelopmentTitle = "0-2 Weeks (Newborn)";
                    DevelopmentText = "• Startles to loud noises\n• Prefers looking at faces (8-12 inches away)\n• Keeps hands in tight fists\n• Reflexive smile (not social yet)\n• Moves head side to side on stomach";
                    pool = [
                        "Is it normal my newborn sleeps all day?",
                        "How often should a 1-week-old feed?",
                        "What is normal for newborn poop?",
                        "How to soothe a crying newborn?",
                        "When to do tummy time?",
                        "Is my baby eating enough?",
                        "How to safely swaddle?",
                        "Tips for burping?"
                    ];
                }
                else if (daysOld < 90)
                {
                    DevelopmentTitle = "1-3 Months";
                    DevelopmentText = "• Social smiles begin (around 2 months)\n• Coos and makes gurgling sounds\n• Turns head toward sounds\n• Follows objects with eyes across midline\n• Holds head up steady during tummy time\n• Brings hands to mouth";
                    pool = [
                        "When will my baby smile?",
                        "Is 4 hours between feeds normal?",
                        "How much tummy time is needed?",
                        "When do they sleep longer at night?",
                        "Signs of a growth spurt?",
                        "What are leap weeks?",
                        "How to establish a bedtime routine?",
                        "Why is my baby so fussy in the evening?"
                    ];
                }
                else if (daysOld < 180)
                {
                    DevelopmentTitle = "3-6 Months";
                    DevelopmentText = "• Laughs out loud and babbles ('ba-ba')\n• Rolls over (front to back, then back to front)\n• Reaches for and grabs objects\n• Passes toys from one hand to the other\n• Bears weight on legs when held standing\n• Begins to sit with support";
                    pool = [
                        "When do babies roll over?",
                        "Signs of a sleep regression?",
                        "Starting solid foods?",
                        "How many naps are typical now?",
                        "Signs of teething?",
                        "When do they start babbling?",
                        "Can they drink water yet?",
                        "Best toys for this age?"
                    ];
                }
                else
                {
                    DevelopmentTitle = "6-12 Months";
                    DevelopmentText = "• Sits independently without support\n• Crawls or scoots\n• Pulls to a standing position\n• Uses pincer grasp (thumb and forefinger)\n• Understands 'no' and recognizes own name\n• Plays peek-a-boo and claps hands";
                    pool = [
                        "When will my baby sit up?",
                        "Is it time for two naps?",
                        "Signs of separation anxiety?",
                        "When do babies crawl?",
                        "Transitioning from breast/bottle to cup?",
                        "Finger foods to try?",
                        "When to drop the pacifier?",
                        "Sleep training tips?"
                    ];
                }
                
                var ticks = timeProvider.GetUtcNow().Ticks;
                foreach (var q in pool.OrderBy(x => (x.GetHashCode() ^ ticks)).Take(4))
                {
                    SuggestedQuestions.Add(q);
                }
                return;
            }
        }
        
        // Fallback questions
        var fallbacks = new[] {
            "Is 4 hours between feeds normal?",
            "When will the baby sleep through the night?",
            "How much tummy time is needed?",
            "Signs of a growth spurt?",
            "How to soothe a crying baby?",
            "Starting solid foods?"
        };
        var fallbackTicks = timeProvider.GetUtcNow().Ticks;
        foreach (var q in fallbacks.OrderBy(x => (x.GetHashCode() ^ fallbackTicks)).Take(4))
        {
            SuggestedQuestions.Add(q);
        }
    }

    private CancellationTokenSource? _cts;

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }

    [RelayCommand]
    private void ResetChat()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        IsBusy = false;
        Messages.Clear();
        HasStartedChat = false;
    }

    [RelayCommand]
    private async Task AskAsync()
    {
        if (string.IsNullOrWhiteSpace(Question) || IsBusy) return;

        var text = Question;
        Question = string.Empty;
        
        Messages.Add(new AdvisorMessage { Text = text, IsUser = true });
        HasStartedChat = true;
        IsBusy = true;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var session = await sessionStore.GetAsync(token);
            if (session == null)
            {
                Messages.Add(new AdvisorMessage { Text = "You must be signed in.", IsUser = false });
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

            try
            {
                var response = await api.AskAsync(request, token);
                if (!token.IsCancellationRequested)
                {
                    Messages.Add(new AdvisorMessage { Text = response?.Answer ?? "Error parsing response.", IsUser = false });
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore cancellation
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    Messages.Add(new AdvisorMessage { Text = $"Error connecting to Advisor API: {ex.Message}", IsUser = false });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Messages.Add(new AdvisorMessage { Text = $"Error connecting to Advisor API: {ex.Message}", IsUser = false });
            }
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsBusy = false;
            }
        }
    }
}

public class AdvisorMessage
{
    public required string Text { get; init; }
    public required bool IsUser { get; init; }
}

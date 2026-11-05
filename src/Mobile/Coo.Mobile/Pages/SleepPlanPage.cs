using Coo.Domain.Insights;
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.SleepPlan;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Pages;

/// <summary>
/// The sleep plan (Spec 019): the questions and a preview before a plan, then the plan itself, its check-ins, the
/// routine and the settling steps, and the past plans.
/// </summary>
public sealed class SleepPlanPage : ContentPage
{
    private static readonly SleepPlanGoal[] AllGoals = Enum.GetValues<SleepPlanGoal>();
    private static readonly SettlingApproach[] AllApproaches = Enum.GetValues<SettlingApproach>();
    private static readonly string[] QuestionKeys = ["Plan.Q.Feeding", "Plan.Q.Weight", "Plan.Q.Breathing"];

    private readonly SleepPlanViewModel _model;
    private readonly ThemeController _theme;
    private readonly ContentView _body = new();
    private readonly ScrollView _scroll;
    private bool _renderQueued;

    public SleepPlanPage(SleepPlanViewModel model, ThemeController theme)
    {
        (_model, _theme) = (model, theme);
        // The plan's words go to the Advisor: its consent, asked once, covers them (Spec 019 §9).
        _model.EnsureConsentAsync = () => AdvisorPage.AskConsentAsync(this);
        _model.HasConsent = () => AdvisorPage.HasConsent;
        Shell.SetNavBarIsVisible(this, false);
        _scroll = new ScrollView { Content = _body };
        Content = Backdrop.Behind(_scroll);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _model.PropertyChanged += OnChanged;
        _theme.PaletteChanged += OnPaletteChanged;
        await _model.LoadAsync();
        QueueRender();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _model.PropertyChanged -= OnChanged;
        _theme.PaletteChanged -= OnPaletteChanged;
    }

    protected override bool OnBackButtonPressed()
    {
        Dispatcher.Dispatch(async () => await Navigation.PopAsync());
        return true;
    }

    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => QueueRender();

    private void OnPaletteChanged(object? sender, PaletteKind e) => QueueRender();

    /// <summary>Several properties change at once (an answer clears the preview): one redraw for them.</summary>
    private void QueueRender()
    {
        if (_renderQueued)
            return;
        _renderQueued = true;
        Dispatcher.Dispatch(() =>
        {
            _renderQueued = false;
            Render();
        });
    }

    private void Render()
    {
        var y = _scroll.ScrollY;
        var stack = new VerticalStackLayout { Spacing = 14, Padding = new Thickness(24, 32, 24, 96) };
        stack.Add(Ui.BackHeader(Strings.Get("Kind.SleepPlan"), () => Navigation.PopAsync()));
        if (!_model.IsReady)
        {
            _body.Content = stack;
            return;
        }

        if (!_model.IsBorn)
            stack.Add(Ui.Text(Strings.Error("baby.not-born-yet"), "Body", "Ink2"));
        else if (_model.Current is { } plan)
            RenderPlan(stack, plan);
        else
            RenderIntake(stack);

        if (_model.History.Count > 0)
        {
            stack.Add(Ui.Text(Strings.Get("Plan.History"), "Section").Tap(l => l.Margin = new Thickness(0, 12, 0, 0)));
            foreach (var past in _model.History)
                stack.Add(new Card(new VerticalStackLayout { Spacing = 2, Children = { Ui.Text(past.Dates, "BodyStrong"), Ui.Text(past.Summary, "Meta", "Ink2") } }));
        }

        _body.Content = stack;
        if (y > 0)
            Dispatcher.Dispatch(async () => await _scroll.ScrollToAsync(0, y, false));
    }

    private void RenderPlan(VerticalStackLayout stack, Coo.Mobile.Application.Insights.SleepPlanToday plan)
    {
        stack.Add(Ui.Text(Strings.Format("Plan.Day", plan.DayNumber), "Title"));
        stack.Add(Ui.Text(SleepPlanViewModel.ApproachName(plan.Approach) + " · " + string.Join(", ", plan.Details.Goals.Select(SleepPlanViewModel.GoalName)), "Meta", "Ink2"));

        if (plan.CheckIn is { } checkIn)
            stack.Add(CheckInCard(checkIn));

        if (plan.Day is { } day)
            stack.Add(DayCard(day));
        else
            stack.Add(new Card(Ui.Text(Strings.Get("Plan.Paused"), "Body")));

        stack.Add(Ui.Text(Strings.Format("Plan.NextCheckIn", SleepPlanViewModel.Date(plan.NextCheckIn)), "Meta", "Ink2"));
        // Premium (Spec 019 §4): the plan in the Advisor's words, above the reviewed texts it was written from.
        if (_model.PlanWords.Length > 0)
            stack.Add(TextCard(Strings.Format("Plan.Personal", _model.BabyName), _model.PlanWords));
        else if (_model.CanWriteWords || _model.IsWriting)
        {
            var write = new Button { Text = Strings.Get(_model.IsWriting ? "Summary.Writing" : "Plan.Write"), IsEnabled = !_model.IsWriting };
            write.SetDynamicResource(StyleProperty, "GhostButton");
            write.Clicked += async (_, _) => await _model.WriteWordsAsync();
            stack.Add(new Card(new VerticalStackLayout
            {
                Spacing = 8,
                Children = { Ui.Text(Strings.Format("Plan.PersonalHint", _model.BabyName), "Body", "Ink2"), write },
            }));
        }

        AddTexts(stack, plan.Approach);
        if (!_model.IsPremium)
            stack.Add(Ui.Text(Strings.Format("Plan.PersonalHint", _model.BabyName), "Meta", "Ink2"));

        if (_model.CanChange)
        {
            var stopButton = new Button { Text = Strings.Get("Plan.Stop"), Margin = new Thickness(0, 8, 0, 0) };
            stopButton.SetDynamicResource(StyleProperty, "GhostButton");
            stopButton.Clicked += async (_, _) =>
            {
                if (await CooDialog.ConfirmAsync(this, Strings.Get("Plan.Stop"), Strings.Get("Plan.StopConfirm"), Strings.Get("Plan.Stop"), Strings.Get("Plan.Keep")))
                    await _model.StopAsync();
            };
            stack.Add(stopButton);
        }
        else
        {
            stack.Add(Ui.Text(Strings.Get("Plan.ReadOnly"), "Meta", "Ink2"));
        }
    }

    private void RenderIntake(VerticalStackLayout stack)
    {
        stack.Add(Ui.Text(Strings.Format("Plan.Intro", _model.BabyName), "Body", "Ink2"));
        if (!_model.CanChange)
        {
            stack.Add(Ui.Text(Strings.Get("Plan.ReadOnly"), "Meta", "Ink2"));
            return;
        }

        stack.Add(Ui.Text(Strings.Get("Plan.Goals"), "Section"));
        stack.Add(Chips([.. AllGoals.Select(g => (SleepPlanViewModel.GoalName(g), (Action)(() => _model.ToggleGoal(g)), _model.Goals.Contains(g)))]));

        stack.Add(Ui.Text(Strings.Format("Plan.Approach", _model.BabyName), "Section"));
        if (_model.IsUnderFourMonths)
            stack.Add(Ui.Text(Strings.Get("Plan.RhythmOnly"), "Body", "Ink2"));
        else
            stack.Add(Chips([.. AllApproaches.Select(a => (SleepPlanViewModel.ApproachName(a), (Action)(() => _model.ChooseApproach(a)), _model.Approach == a))]));

        stack.Add(Ui.Text(Strings.Get("Plan.WakeUp"), "Section"));
        var picker = new TimePicker { Time = _model.WakeUp ?? new TimeSpan(7, 0, 0), VerticalOptions = LayoutOptions.Center };
        picker.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TimePicker.Time) && picker.Time is { } chosen && chosen != _model.WakeUp)
                _model.ChooseWakeUp(chosen);
        };
        var usual = new Button { Text = Strings.Get("Plan.WakeUpUsual"), Margin = new Thickness(0, 0, 8, 0) };
        usual.SetDynamicResource(StyleProperty, _model.WakeUp is null ? "PrimaryButton" : "GhostButton");
        usual.Clicked += (_, _) => _model.ChooseWakeUp(null);
        stack.Add(new HorizontalStackLayout { Spacing = 8, Children = { usual, picker } });

        stack.Add(Ui.Text(Strings.Get("Plan.Safety"), "Section"));
        for (var i = 0; i < QuestionKeys.Length; i++)
        {
            var question = i;
            stack.Add(Ui.Text(Strings.Get(QuestionKeys[i]), "Body"));
            stack.Add(Chips([
                (Strings.Get("Plan.No"), () => _model.Answer(question, false), _model.Concern(question) == false),
                (Strings.Get("Plan.Yes"), () => _model.Answer(question, true), _model.Concern(question) == true)]));
        }

        if (!string.IsNullOrEmpty(_model.Refusal))
        {
            stack.Add(new Card(Ui.Text(_model.Refusal, "Body")));
            return;
        }

        if (_model.PreviewDay is not { } day)
        {
            var see = new Button { Text = Strings.Get("Plan.See"), IsEnabled = _model.CanPreview };
            see.SetDynamicResource(StyleProperty, "PrimaryButton");
            see.Clicked += async (_, _) => await _model.PreviewAsync();
            stack.Add(see);
            if (!_model.CanPreview)
                stack.Add(Ui.Text(Strings.Get("Plan.Answer"), "Meta", "Ink2"));
            return;
        }

        stack.Add(DayCard(day));
        AddTexts(stack, SleepPlanner.ApproachFor(_model.Approach, _model.IsUnderFourMonths ? 0 : SleepPlanner.TrainingFromDays));
        var startButton = new Button { Text = Strings.Get("Plan.Start") };
        startButton.SetDynamicResource(StyleProperty, "PrimaryButton");
        startButton.Clicked += async (_, _) =>
        {
            if (await _model.StartAsync())
                await _scroll.ScrollToAsync(0, 0, true);
        };
        stack.Add(startButton);
    }

    /// <summary>The planned day: one row per wake-up, nap and bedtime, then why the times are what they are.</summary>
    private Card DayCard(SleepPlanDay day)
    {
        var rows = new VerticalStackLayout { Spacing = 12 };
        var look = EventAppearance.For("sleep");
        foreach (var row in _model.Rows(day))
        {
            var line = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 10 };
            var label = new VerticalStackLayout { Spacing = 2, Children = { Ui.Text(row.Label, "BodyStrong") } };
            if (row.Detail.Length > 0)
                label.Add(Ui.Text(row.Detail, "Meta", "Ink2"));
            line.Add(label, 0);
            line.Add(Ui.Text(row.Time, "BodyStrong", look.Ink).Tap(l => l.VerticalOptions = LayoutOptions.Center), 1);
            rows.Add(line);
        }

        foreach (var note in _model.Notes(day))
            rows.Add(Ui.Text(note, "Meta", "Ink2"));
        return new Card(rows);
    }

    private View CheckInCard(SleepCheckIn checkIn)
    {
        var stack = new VerticalStackLayout { Spacing = 8 };
        stack.Add(Ui.Text(Strings.Get("Plan.CheckIn"), "BodyStrong"));
        stack.Add(Ui.Text(Strings.Get("Plan.CheckInHint"), "Meta", "Ink2"));
        var table = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)], ColumnSpacing = 14, RowSpacing = 6 };
        var measures = _model.Measures(checkIn);
        table.Add(Ui.Text(Strings.Get("Plan.Before"), "Meta", "Ink2"), 1, 0);
        table.Add(Ui.Text(Strings.Get("Plan.Now"), "Meta", "Ink2"), 2, 0);
        for (var i = 0; i < measures.Count; i++)
        {
            table.AddRowDefinition(new RowDefinition(GridLength.Auto));
            table.Add(Ui.Text(measures[i].Label, "Body"), 0, i + 1);
            table.Add(Ui.Text(measures[i].Before, "Body", "Ink2"), 1, i + 1);
            table.Add(Ui.Text(measures[i].Now, "BodyStrong"), 2, i + 1);
        }

        table.AddRowDefinition(new RowDefinition(GridLength.Auto));
        stack.Add(table);
        foreach (var paragraph in _model.CheckInWords.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            stack.Add(Ui.Text(paragraph, "Body"));
        stack.Add(Ui.Text(_model.Suggestion(checkIn.Suggestion), "BodyStrong"));
        if (_model.CanChange)
        {
            var buttons = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
            if (checkIn.Suggestion.Kind == PlanAdjustmentKind.KeepGoing)
            {
                buttons.Add(Button(Strings.Get("Plan.Ok"), "PrimaryButton", () => _model.AnswerCheckInAsync(false)));
            }
            else
            {
                buttons.Add(Button(Strings.Get("Plan.Accept"), "PrimaryButton", () => _model.AnswerCheckInAsync(true)));
                buttons.Add(Button(Strings.Get("Plan.Keep"), "GhostButton", () => _model.AnswerCheckInAsync(false)));
            }

            stack.Add(buttons);
        }

        return Ui.Rounded(stack, "AccentSoft", 22, new Thickness(20, 18));
    }

    /// <summary>The stored texts (Spec 019 §5): routine, settling for the approach, what to expect, safe sleep.</summary>
    private void AddTexts(VerticalStackLayout stack, SettlingApproach approach)
    {
        stack.Add(TextCard(Strings.Get("Plan.Routine"), _model.RoutineText));
        stack.Add(TextCard(Strings.Get("Plan.Settling") + " · " + SleepPlanViewModel.ApproachName(approach), _model.SettlingText(approach)));
        stack.Add(TextCard(Strings.Get("Plan.Expect"), SleepPlanViewModel.ExpectText(approach)));
        stack.Add(Ui.Text(Strings.Get("Plan.SafeSleep"), "Body"));
        stack.Add(Ui.Text(Strings.Get("Plan.Guidance"), "Meta", "Ink2").Tap(l => l.FontSize = 11));
    }

    private static Card TextCard(string title, string text)
    {
        var stack = new VerticalStackLayout { Spacing = 8, Children = { Ui.Text(title, "BodyStrong") } };
        foreach (var paragraph in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            stack.Add(Ui.Text(paragraph, "Body", "Ink2"));
        return new Card(stack);
    }

    private static Button Button(string text, string style, Func<Task> act)
    {
        var button = new Button { Text = text, Margin = new Thickness(0, 0, 8, 8) };
        button.SetDynamicResource(StyleProperty, style);
        button.Clicked += async (_, _) => await act();
        return button;
    }

    private static FlexLayout Chips((string Text, Action Choose, bool Selected)[] chips)
    {
        var row = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        foreach (var (text, choose, selected) in chips)
        {
            var chip = new Button { Text = text, Margin = new Thickness(0, 0, 8, 8) };
            chip.SetDynamicResource(StyleProperty, selected ? "PrimaryButton" : "GhostButton");
            chip.Clicked += (_, _) => choose();
            row.Add(chip);
        }

        return row;
    }
}

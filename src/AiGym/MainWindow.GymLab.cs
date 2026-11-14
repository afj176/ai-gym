using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AiGym;

// Agent lab: a model proposes a bounded plan; every tool step waits for an explicit review of its
// exact arguments. The engine (LabEngine, GymLabServices, LabStore) is unchanged.
public sealed partial class MainWindow
{
    static string PrettyJson(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        catch { return text; }
    }

    /// <summary>Output limit for a proposed plan; lowered when the planning prompt leaves less room in the loaded context.</summary>
    const int PlanOutputTokens = 2048;

    // Words for the engine's states; the enum value itself stays visible in the tooltip.
    static string LabStateLabel(LabState state) => state switch
    {
        LabState.AwaitingReview => "Awaiting review",
        LabState.SetupRequired => "Setup required",
        _ => state.ToString()
    };

    FrameworkElement BuildGymLab()
    {
        var store = new LabStore(Store.Root);
        LabRun? currentRun = null;
        GymLabServices? services = null;
        bool updating = false, planning = false;

        var (noticeView, notify) = GymNotice();
        var models = new ComboBox { Header = "Planning model", DisplayMemberPath = "DisplayName", HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Choose a saved connection" };
        var datasets = new ComboBox { Header = "Dataset · optional", DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "No dataset bound" };
        var prompt = new TextBox { Header = "Experiment objective", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96, MaxLength = 4000, PlaceholderText = "Check this endpoint, then inspect my dataset for duplicate conversations…" };
        var history = new ComboBox { Header = "Saved experiment", DisplayMemberPath = "Title", HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Open a saved experiment" };
        var plan = Button("Propose plan", (_, _) => { }, true);
        var cancel = Button("Cancel", (_, _) => gymOperation?.Cancel());
        var refresh = Button("Refresh resources", (_, _) => { });
        var planReason = Muted("", 12);
        var thinking = BrandControl(new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed });
        AutomationProperties.SetName(thinking, "Waiting for the planning model");

        var planning1 = StepCard("Step 1", "Design the experiment", "Select a planning model, optionally bind a dataset, and describe the objective. The model only proposes steps; nothing runs until you approve it.",
            FormGrid(models, datasets), prompt,
            new ScrollViewer { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { plan, cancel, refresh } }, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled },
            planReason, thinking);

        // Review area.
        var runTitle = Heading("", 17);
        var runState = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        var binding = Muted("", 12);
        var steps = new StackPanel { Spacing = 10 };
        var approved = BrandControl(new CheckBox { Content = "I reviewed the next step's exact arguments and approve this invocation" });
        var execute = Button("Run reviewed next step", (_, _) => { }, true);
        var executeReason = Muted("", 12);
        executeReason.VerticalAlignment = VerticalAlignment.Center;
        var approval = new StackPanel { Spacing = 10, Children = { approved, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { execute, executeReason } } } };
        var approvalCard = Surface(approval, "GymTintBrush", 14, 16, border: false);
        var runView = new StackPanel
        {
            Spacing = 14,
            Children = { new StackPanel { Spacing = 4, Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { runState, runTitle } }, binding } }, steps, approvalCard }
        };
        var empty = EmptyState("let-me-think", "No experiment open", "Propose a plan above, or open a saved experiment. Each proposed step shows its tool and exact arguments before anything runs.");
        // Spring reacts on the empty panel: thinking while the model plans, encouraging after a failure.
        var plainNotify = notify;
        notify = (message, severity) =>
        {
            plainNotify(message, severity);
            if (severity == InfoBarSeverity.Error) PlayReaction(empty.Art, "try-again");
        };
        // While the planning model works on a first plan, proposed steps appear as skeletons under Spring's thinking face.
        FrameworkElement StepSkeleton(double title) => Surface(new StackPanel { Spacing = 12, Children = { Fluent.Skeleton(title, .3), Surface(Fluent.Skeleton(.6, .9, .45), "GymSurfaceAltBrush", 8, 12, border: false) } }, "GymSurfaceBrush", 14, 16);
        var planSkeleton = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed, Children = { StepSkeleton(.55), StepSkeleton(.4), StepSkeleton(.5) } };
        AutomationProperties.SetName(planSkeleton, "Waiting for proposed steps");
        var review = StepCard("Step 2", "Review each step", "Every tool invocation requires approval of its exact arguments.", history, runView, empty.View, planSkeleton);
        Guid? shownRun = null;

        LabStep? NextStep() => currentRun?.Steps.FirstOrDefault(s => s.State != LabState.Completed);

        void UpdateActions()
        {
            bool busy = gymOperation != null;
            plan.IsEnabled = !busy && models.SelectedItem != null && prompt.Text.Trim().Length > 0;
            cancel.IsEnabled = busy;
            refresh.IsEnabled = !busy;
            history.IsEnabled = !busy;
            planReason.Text = busy ? (planning ? "Waiting for the planning model. Cancel stops the request." : "A lab operation is active.")
                : models.SelectedItem == null ? "Choose a planning model. Saved connections that answer text prompts are listed here."
                : prompt.Text.Trim().Length == 0 ? "Describe the experiment objective." : "";
            planReason.Visibility = planReason.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            thinking.Visibility = busy && planning ? Visibility.Visible : Visibility.Collapsed;
            planSkeleton.Visibility = busy && planning && currentRun == null ? Visibility.Visible : Visibility.Collapsed;
            var next = NextStep();
            approved.IsEnabled = !busy && next != null;
            execute.IsEnabled = !busy && next != null && approved.IsChecked == true && services != null;
            executeReason.Text = next == null ? "" : busy ? "Step running…" : approved.IsChecked != true ? "Tick the review box to enable this step." : "";
        }
        gymBusyListeners.Add(UpdateActions);

        FrameworkElement StepView(LabStep step, int index, bool isNext)
        {
            var number = Label((index + 1).ToString(), 12, isNext ? "GymOnAccentBrush" : "GymMutedBrush");
            number.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            number.HorizontalAlignment = HorizontalAlignment.Center;
            number.VerticalAlignment = VerticalAlignment.Center;
            var dot = Surface(number, isNext ? "GymAccentBrush" : "GymSurfaceAltBrush", 13, 0, border: !isNext);
            dot.Width = dot.Height = 26;
            dot.VerticalAlignment = VerticalAlignment.Top;
            var title = Label(step.Title, 15);
            title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            var pill = Pill(LabStateLabel(step.State), step.State == LabState.Ready ? GymTone.Neutral : StateTone(step.State.ToString()));
            ToolTipService.SetToolTip(pill, "Engine state · " + step.State);
            var head = new Grid { ColumnSpacing = 10 };
            head.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            head.Children.Add(title);
            Grid.SetColumn(pill, 1);
            head.Children.Add(pill);
            var tool = Muted("Tool · " + step.Tool, 12);
            tool.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas");
            var body = new StackPanel { Spacing = 8, Children = { head, tool } };
            if (isNext) body.Children.Insert(0, Eyebrow("Next step · waiting for your review"));
            body.Children.Add(new StackPanel { Spacing = 3, Children = { Muted("Exact arguments", 12), MonoBlock(PrettyJson(step.Arguments), 180) } });
            if (step.Result is { } result)
            {
                body.Children.Add(Label(result.Summary, 13.5));
                if (result.Qualification.Length > 0) body.Children.Add(Muted(result.Qualification, 12));
                if (result.Data.Length > 2)
                {
                    body.Children.Add(new Expander { Header = "Result data · " + result.Kind, Content = MonoBlock(PrettyJson(result.Data), 260), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
                }
            }
            if (step.Error.Length > 0) body.Children.Add(Label(step.Error, 13, "GymDangerBrush"));
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(dot);
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            var card = Surface(row, "GymSurfaceBrush", 14, 16, borderBrush: isNext ? "GymAccentBrush" : "GymBorderBrush");
            if (isNext) card.BorderThickness = new Thickness(2);
            AutomationProperties.SetName(card, $"Step {index + 1}: {step.Title}, {LabStateLabel(step.State)}");
            return card;
        }

        void Show()
        {
            approved.IsChecked = false;
            steps.Children.Clear();
            runView.Visibility = currentRun == null ? Visibility.Collapsed : Visibility.Visible;
            empty.View.Visibility = currentRun == null ? Visibility.Visible : Visibility.Collapsed;
            if (currentRun == null) { shownRun = null; UpdateActions(); return; }
            var next = NextStep();
            runTitle.Text = currentRun.Title;
            runState.Content = Pill(LabStateLabel(currentRun.State), StateTone(currentRun.State.ToString()));
            try
            {
                // The bound model and dataset, as recorded when the experiment was created.
                var bound = JsonSerializer.Deserialize<GymLabContext>(currentRun.Context);
                binding.Text = bound?.Agent == null ? "" : $"Bound model · {bound.Agent.Name} ({bound.Agent.Model} at {bound.Agent.Endpoint})" + (bound.DatasetId is { } data ? $" · dataset {data.ToString()[..8]} (source {ShortHash(bound.DatasetHash ?? "")})" : " · no dataset bound");
            }
            catch { binding.Text = ""; }
            foreach (var (step, index) in currentRun.Steps.Select((step, index) => (step, index))) steps.Children.Add(StepView(step, index, step == next));
            // A newly opened experiment settles in; re-rendering the same one after a step keeps it still.
            if (shownRun != currentRun.Id) { shownRun = currentRun.Id; Fluent.Appear(runView, 8); }
            approvalCard.Visibility = next == null ? Visibility.Collapsed : Visibility.Visible;
            if (next == null)
            {
                var done = Reaction("that-worked", 96);
                var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { Heading("Experiment completed", 17), Muted("Every step ran after your review. Open a step's result data for the retained output.", 13) } };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { done, text } };
                steps.Children.Add(Surface(row, "GymTintBrush", 14, 14, border: false));
            }
            UpdateActions();
        }

        void Save()
        {
            if (currentRun != null) store.Save(currentRun);
            Show();
        }

        void Refresh()
        {
            var keepModel = (models.SelectedItem as AiEndpointProfile)?.Id;
            var keepData = (datasets.SelectedItem as GymDataset)?.Id;
            var choices = AiModelChoices.For(Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? [], AiCapability.Chat).ToList();
            models.ItemsSource = choices;
            models.SelectedItem = choices.FirstOrDefault(x => x.Id == keepModel);
            var sets = GymDatasets.List(Store.Root).ToArray();
            datasets.ItemsSource = sets;
            datasets.SelectedItem = sets.FirstOrDefault(x => x.Id == keepData);
            updating = true;
            try
            {
                var runs = store.Load();
                history.ItemsSource = runs;
                history.SelectedItem = runs.FirstOrDefault(x => x.Id == currentRun?.Id);
                history.Visibility = runs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            finally { updating = false; }
            UpdateActions();
        }
        void SafeRefresh()
        {
            try { Refresh(); }
            catch (Exception e) { notify(e.Message, InfoBarSeverity.Error); }
        }

        string Secret(AiEndpointProfile profile)
        {
            // Checked even when a session key exists: a restarted managed model keeps its connection ID but gets a new
            // model ID, and the experiment must not send to it under the old identity.
            var saved = (Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? []).SingleOrDefault(p => p.Id == profile.Id && p.Endpoint == profile.Endpoint && p.Model == profile.Model)
                ?? throw new InvalidOperationException($"The model connection this experiment was created with ({profile.Model} at {profile.Endpoint}) is no longer saved, or its endpoint or model changed. Restore that connection, or propose a new experiment with a current one.");
            return aiSessionKeys.GetValueOrDefault(profile.Id) ?? Store.Unprotect(saved.Secret);
        }

        history.SelectionChanged += (_, _) =>
        {
            if (updating || gymOperation != null) return;
            try
            {
                if (history.SelectedItem is LabRun run)
                {
                    var bound = JsonSerializer.Deserialize<GymLabContext>(run.Context) ?? throw new InvalidDataException("This experiment has no saved model and dataset context, so it cannot be reopened here.");
                    if (bound.Agent == null) throw new InvalidDataException("This experiment has no AI Gym model binding.");
                    services = new(Store.Root, bound, Secret);
                    currentRun = run;
                    Show();
                }
            }
            catch (Exception e)
            {
                notify(e.Message, InfoBarSeverity.Error);
                currentRun = null;
                services = null;
                Show();
            }
        };

        plan.Click += async (_, _) =>
        {
            planning = true;
            PlayReaction(empty.Art, "let-me-think", animate: true);
            await GymRun(notify, async ct =>
            {
                if (models.SelectedItem is not AiEndpointProfile profile) throw new ArgumentException("Choose a planning model first.");
                var dataset = datasets.SelectedItem as GymDataset;
                var bound = new GymLabContext(profile with { Secret = "" }, dataset?.Id, dataset?.SourceHash);
                var selectedServices = new GymLabServices(Store.Root, bound, Secret);
                var objective = prompt.Text;
                var messages = selectedServices.Plan(objective);
                var response = new StringBuilder();
                using var client = new LocalAiClient();
                var key = Secret(profile);
                int output = await client.FitOutput(profile, key, messages, PlanOutputTokens, ct);
                var answer = await client.StreamChat(profile, key, messages, output, x => response.Append(x), ct);
                if (answer.ObservedModel is { } observed && observed != profile.Model) throw new InvalidDataException("Planning model identity changed.");
                var planSteps = selectedServices.Engine.ParsePlan(response.ToString(), answer.FinishReason ?? "");
                currentRun = new() { Prompt = objective, Title = objective[..Math.Min(80, objective.Length)], Scope = "ai-gym-local", Context = JsonSerializer.Serialize(bound), State = LabState.AwaitingReview, Steps = planSteps };
                services = selectedServices;
                Save();
                Refresh();
                notify($"Plan proposed with {planSteps.Count} step{(planSteps.Count == 1 ? "" : "s")}. Nothing has run; review the first step below.", InfoBarSeverity.Informational);
            });
            planning = false;
            UpdateActions();
        };

        execute.Click += async (_, _) =>
        {
            if (approved.IsChecked != true || currentRun == null || services == null) { notify("Review and approve a proposed next step first.", InfoBarSeverity.Warning); return; }
            var run = currentRun;
            var executor = services;
            var next = run.Steps.FirstOrDefault(s => s.State != LabState.Completed);
            if (next == null) return;
            approved.IsChecked = false;
            await GymRun(notify, async ct =>
            {
                await executor.Engine.Execute(run, next, "ai-gym-local", true, Save, ct);
                Show();
            });
            // The engine records a cancelled or failed step on the step itself instead of throwing, so say what happened
            // here; otherwise the notice above still reads "Plan proposed … Nothing has run".
            int number = run.Steps.IndexOf(next) + 1;
            switch (next.State)
            {
                case LabState.Completed: notify($"Step {number} completed. Review its result below.", InfoBarSeverity.Success); break;
                case LabState.Cancelled: notify($"Step {number} was cancelled. Completed steps are kept; approve it again to retry.", InfoBarSeverity.Informational); break;
                case LabState.Failed: notify($"Step {number} failed: {next.Error}", InfoBarSeverity.Error); break;
                case LabState.SetupRequired: notify($"Step {number} needs setup: {next.Error}", InfoBarSeverity.Warning); break;
            }
            UpdateActions();
        };

        approved.Checked += (_, _) => UpdateActions();
        approved.Unchecked += (_, _) => UpdateActions();
        models.SelectionChanged += (_, _) => UpdateActions();
        prompt.TextChanged += (_, _) => UpdateActions();
        refresh.Click += (_, _) => SafeRefresh();
        refreshGymDatasets += () => { if (gymOperation == null) SafeRefresh(); };

        SafeRefresh();
        Show();
        var page = new StackPanel { Spacing = 14, Margin = new Thickness(0, 0, 4, 16), Children = { noticeView, planning1, review } };
        return PageLayout("Agent lab", "Plan bounded model and dataset experiments. Review every tool invocation before it runs.", VerticalScroll(page));
    }
}

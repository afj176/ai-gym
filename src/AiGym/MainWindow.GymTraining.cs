using System.Data;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AiGym;

// Training lab: inputs and settings on the left, the retained run and its evidence on the right.
// The training engine (LocalTraining) is unchanged; this page only presents what it writes to disk.
public sealed partial class MainWindow
{
    /// <summary>One retained run folder, summarised from its config.json and status.json.</summary>
    sealed record TrainingRunItem(Guid Id, string State, string Base, DateTime Modified)
    {
        public override string ToString() => $"{Id.ToString()[..8]} · {State}{(Base.Length > 0 ? " · " + Base : "")} · {Modified.ToLocalTime():g}";
    }

    static List<TrainingRunItem> TrainingRuns()
    {
        var folder = Path.Combine(Store.Root, "agent-labs", "training");
        var runs = new List<TrainingRunItem>();
        if (!Directory.Exists(folder)) return runs;
        foreach (var path in Directory.GetDirectories(folder))
        {
            if (!Guid.TryParse(Path.GetFileName(path), out var id)) continue;
            string basis = "";
            try
            {
                var config = Path.Combine(path, "config.json");
                if (File.Exists(config) && JsonSerializer.Deserialize<LocalTrainingSpec>(File.ReadAllText(config)) is { } spec) basis = Path.GetFileName(spec.Base.TrimEnd('\\', '/'));
            }
            catch { }
            runs.Add(new(id, TrainingRunState(path), basis, Directory.GetLastWriteTimeUtc(path)));
        }
        return runs.OrderByDescending(x => x.Modified).ToList();
    }

    FrameworkElement BuildGymTraining()
    {
        ModelLibrarySettings? settings = null;
        string? settingsError = null;
        try { settings = Store.Read<ModelLibrarySettings>("model-library.json"); }
        catch (Exception error) { settingsError = "Model settings could not be read: " + error.Message; }

        var (noticeView, notify) = GymNotice();
        if (settingsError != null) notify(settingsError, InfoBarSeverity.Warning);

        // Step 1 · inputs.
        var python = new TextBox { Header = "Python runtime", Text = settings?.Python ?? "", PlaceholderText = "Path to python.exe" };
        var basis = new TextBox { Header = "Local Transformers base model", PlaceholderText = "Folder containing config.json" };
        var bundle = new TextBox { Header = "Reviewed training bundle", PlaceholderText = "Folder exported from Datasets" };
        FrameworkElement Browse(TextBox box, string name, bool folder, string extension = ".jsonl")
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var pick = IconButton(folder ? "" : "", name, async (_, _) =>
            {
                try { await GymPick(box, folder, extension); }
                catch (Exception e) { notify(e.Message, InfoBarSeverity.Error); }
            });
            row.Children.Add(box);
            Grid.SetColumn(pick, 1);
            row.Children.Add(pick);
            return row;
        }
        var inputs = StepCard("Step 1", "Training inputs", "Choose the runtime, base model and reviewed dataset bundle. Your base weights remain intact.",
            Browse(python, "Choose Python", false, ".exe"), Browse(basis, "Choose base model folder", true), Browse(bundle, "Choose training bundle folder", true));

        // Step 2 · settings.
        var steps = new NumberBox { Header = "Optimizer steps", Value = 20, Minimum = 1, Maximum = 100000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var context = new NumberBox { Header = "Sequence length", Value = 512, Minimum = 16, Maximum = 4096, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var rank = new NumberBox { Header = "LoRA rank", Value = 8, Minimum = 1, Maximum = 64, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var device = new ComboBox { Header = "Device", ItemsSource = new[] { "cpu", "cuda" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var prepare = Button("Check & prepare", (_, _) => { }, true);
        var prepareHint = Muted("Checks the runtime, base model and bundle, then creates a run you can start. No optimizer step runs yet.", 12);
        var parameters = StepCard("Step 2", "Training settings", "Configure the adapter and execution device before preparing a run.",
            FlowGrid(150, steps, context, rank, device), new StackPanel { Spacing = 8, Children = { prepare, prepareHint } });

        // Step 3 · run and inspect.
        Guid? selected = null;
        Guid? running = null;
        bool updating = false;
        DateTimeOffset lastLiveRefresh = DateTimeOffset.MinValue;

        var history = new ComboBox { Header = "Retained training run", HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Choose a run" };
        var statePill = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        var liveLine = Label("", 13);
        liveLine.VerticalAlignment = VerticalAlignment.Center;
        var progress = BrandControl(new ProgressBar { Minimum = 0, Maximum = 1, Value = 0, Visibility = Visibility.Collapsed });
        AutomationProperties.SetName(progress, "Training progress");
        var start = Button("Train / resume", (_, _) => { }, true);
        var serve = Button("Serve adapter", (_, _) => { });
        var stop = Button("Stop at checkpoint", (_, _) => gymOperation?.Cancel());
        ToolTipService.SetToolTip(stop, "Asks the runtime to stop at the next optimizer boundary. Committed checkpoints are kept.");
        var cancelServe = Button("Cancel serving", (_, _) => gymOperation?.Cancel());
        ToolTipService.SetToolTip(cancelServe, "Stops starting the adapter. Its process is ended; the trained adapter stays in the run folder.");
        cancelServe.Visibility = Visibility.Collapsed;
        bool serving = false;
        var actionReason = Muted("", 12);
        var stateTile = StatTile("State");
        var stepTile = StatTile("Optimizer step");
        var lossTile = StatTile("Latest training loss");
        var elapsedTile = StatTile("Elapsed");
        // Wide enough for the longest state word ("Interrupted", "Not started") at the tile's value size.
        var tiles = FlowGrid(190, stateTile.View, stepTile.View, lossTile.View, elapsedTile.View);
        var configLine = Muted("", 12);
        var chart = new ChartView(new ChartDefinition(Guid.NewGuid(), Kind: "Line", X: "step", Values: ["training loss", "held-out loss"], Aggregate: "Raw", Axis: "Numeric", Title: "Loss per optimizer step", Palette: "Gym"))
        {
            Telemetry = true, Compact = true, Height = 300, WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this)
        };
        var chartCard = Surface(chart, "GymSurfaceBrush", 12, 6);
        var chartEmpty = Muted("No optimizer steps recorded for this run yet. The loss chart appears after the first step.", 13);
        var checkpoints = new StackPanel { Spacing = 6 };
        var folderHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var raw = MonoBlock("", 320);
        var rawExpander = new Expander { Header = "Raw evidence (JSON)", Content = raw, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var celebrate = Reaction("that-worked", 84);
        celebrate.Visibility = Visibility.Collapsed;
        celebrate.HorizontalAlignment = HorizontalAlignment.Right;

        var stateRow = new Grid { ColumnSpacing = 10 };
        stateRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        stateRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        stateRow.Children.Add(statePill);
        Grid.SetColumn(liveLine, 1);
        stateRow.Children.Add(liveLine);
        var evidence = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                stateRow, progress, tiles, configLine, chartCard, chartEmpty,
                new StackPanel { Spacing = 6, Children = { Label("Latest checkpoints", 13), checkpoints } },
                folderHost, rawExpander
            }
        };
        var empty = EmptyState("you-got-this", "No training runs yet", "Fill in the inputs, then choose Check & prepare. A prepared run can be started, stopped at a checkpoint and resumed later.");
        var actions = new ScrollViewer
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { start, serve, stop, cancelServe } },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var actionRow = new Grid { ColumnSpacing = 12 };
        actionRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        actionRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        actionRow.Children.Add(new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { actions, actionReason } });
        Grid.SetColumn(celebrate, 1);
        actionRow.Children.Add(celebrate);
        // While a run's evidence is read from disk, a skeleton in its shape stands in (state, figures, chart).
        var evidenceLoading = new StackPanel { Spacing = 14, Visibility = Visibility.Collapsed, Children = { Fluent.Skeleton(.3, .7), FlowGrid(190, Enumerable.Range(0, 4).Select(_ => (FrameworkElement)Surface(Fluent.SkeletonValue(), "GymSurfaceAltBrush", 12, 14, border: false)).ToArray()), Surface(Fluent.Skeleton(.35, .95, .9, .8, .95, .7), "GymSurfaceBrush", 12, 16) } };
        var runCard = StepCard("Step 3", "Run and inspect", "Select a retained run to train, resume, inspect or serve. Evidence stays in the run folder.", history, actionRow, evidence, evidenceLoading, empty.View);

        string RunFolder(Guid id) => LocalTraining.DirectoryFor(Store.Root, id);

        void UpdateActions()
        {
            bool busy = gymOperation != null;
            bool hasRun = selected != null;
            bool servable = hasRun && File.Exists(Path.Combine(RunFolder(selected!.Value), "adapter-manifest.json"));
            prepare.IsEnabled = !busy;
            start.IsEnabled = hasRun && !busy;
            serve.IsEnabled = servable && !busy;
            stop.IsEnabled = busy && running != null;
            stop.Visibility = serving ? Visibility.Collapsed : Visibility.Visible;
            cancelServe.Visibility = serving ? Visibility.Visible : Visibility.Collapsed;
            history.IsEnabled = !busy;
            actionReason.Text = busy ? (running != null ? "A run is active. Stop at checkpoint keeps committed work." : serving ? "Starting the adapter. Cancel serving stops it." : "Another lab operation is active.")
                : !hasRun ? "Prepare or choose a retained run to enable training."
                : !servable ? "Serve adapter becomes available once this run has written its adapter."
                : "";
        }
        gymBusyListeners.Add(UpdateActions);

        // Renders the evidence object the engine already produces; nothing here is derived beyond formatting.
        void ShowEvidence(Guid id, JsonElement e)
        {
            var status = e.GetProperty("Status");
            string state = status.TryGetProperty("state", out var s) ? s.GetString() ?? "Unknown" : "Unknown";
            statePill.Content = Pill(state, StateTone(state));
            stateTile.Set(state, "");
            if (running != id)
            {
                liveLine.Text = status.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString()!
                    : status.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()! : "";
            }

            int? total = null;
            string config = "";
            try
            {
                var path = Path.Combine(RunFolder(id), "config.json");
                if (File.Exists(path) && JsonSerializer.Deserialize<LocalTrainingSpec>(File.ReadAllText(path)) is { } spec)
                {
                    total = spec.Steps;
                    config = $"Base {Path.GetFileName(spec.Base.TrimEnd('\\', '/'))} · bundle {Path.GetFileName(spec.Bundle.TrimEnd('\\', '/'))} · {spec.Steps:N0} steps · sequence {spec.SequenceLength} · rank {spec.Rank} · learning rate {spec.LearningRate} · {spec.Device} · seed {spec.Seed}";
                }
            }
            catch { }
            configLine.Text = config;
            configLine.Visibility = config.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

            var metrics = e.GetProperty("Metrics").EnumerateArray().ToArray();
            var validations = e.GetProperty("ValidationMetrics").EnumerateArray().ToDictionary(x => x.GetProperty("step").GetInt32(), x => x.GetProperty("loss").GetDouble());
            int? step = status.TryGetProperty("step", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : metrics.Length > 0 ? metrics[^1].GetProperty("step").GetInt32() : null;
            stepTile.Set(step == null ? "—" : total == null ? step.Value.ToString("N0") : $"{step:N0} / {total:N0}", "");
            lossTile.Set(metrics.Length > 0 ? metrics[^1].GetProperty("loss").GetDouble().ToString("0.0000") : "—", metrics.Length > 0 ? $"at step {metrics[^1].GetProperty("step").GetInt32():N0}" : "");
            double? elapsed = status.TryGetProperty("elapsedSeconds", out var el) && el.ValueKind == JsonValueKind.Number ? el.GetDouble() : metrics.Length > 0 ? metrics[^1].GetProperty("elapsedSeconds").GetDouble() : null;
            elapsedTile.Set(elapsed == null ? "—" : Duration(elapsed.Value), "");

            if (running != id)
            {
                bool measurable = step != null && total is > 0 && state is "Training" or "Interrupted" or "Completed";
                progress.IsIndeterminate = false;
                progress.Visibility = measurable ? Visibility.Visible : Visibility.Collapsed;
                if (measurable) { progress.Maximum = total!.Value; progress.Value = Math.Min(step!.Value, total.Value); }
            }

            // Loss chart: one row per retained optimizer step; held-out loss only where it was measured.
            var table = new DataTable();
            table.Columns.Add("step", typeof(int));
            table.Columns.Add("training loss", typeof(double));
            table.Columns.Add("held-out loss", typeof(double));
            foreach (var m in metrics)
            {
                int at = m.GetProperty("step").GetInt32();
                table.Rows.Add(at, m.GetProperty("loss").GetDouble(), validations.TryGetValue(at, out var held) ? held : DBNull.Value);
            }
            chartCard.Visibility = metrics.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            chartEmpty.Visibility = metrics.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
            chart.CaptionOverride = $"{metrics.Length:N0} retained optimizer observations · {validations.Count:N0} held-out measurements · {e.GetProperty("MetricsWindow").GetString()}";
            chart.Bind(table, false, table.Rows.Count);

            checkpoints.Children.Clear();
            foreach (var c in e.GetProperty("Checkpoints").EnumerateArray())
            {
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                string at = c.GetProperty("Step").ValueKind == JsonValueKind.Number ? "step " + c.GetProperty("Step").GetInt32().ToString("N0") : "step unknown";
                row.Children.Add(Label($"{c.GetProperty("Directory").GetString()} · {at} · saved {c.GetProperty("SavedAt").GetDateTime().ToLocalTime():g}", 13));
                var note = Muted(c.GetProperty("State").GetString() ?? "", 12);
                Grid.SetColumn(note, 1);
                row.Children.Add(note);
                checkpoints.Children.Add(Surface(row, "GymSurfaceAltBrush", 8, 10, border: false));
            }
            if (checkpoints.Children.Count == 0) checkpoints.Children.Add(Muted("No committed checkpoint observed yet.", 13));

            folderHost.Content = ValueRow("Evidence folder", e.GetProperty("Folder").GetString() ?? "", folder: true);
            SetMono(raw, JsonSerializer.Serialize(e, new JsonSerializerOptions { WriteIndented = true }));
        }

        Guid? shownRun = null;
        async Task ShowRun(Guid? id)
        {
            // The previous run's evidence never shows under a new selection: a skeleton holds its place until the read
            // lands. Re-reading the run already on screen (after an operation) keeps it in place.
            bool same = id != null && id == shownRun && evidence.Visibility == Visibility.Visible;
            if (!same) evidence.Visibility = Visibility.Collapsed;
            evidenceLoading.Visibility = id == null || same ? Visibility.Collapsed : Visibility.Visible;
            empty.View.Visibility = id == null ? Visibility.Visible : Visibility.Collapsed;
            if (id == null) shownRun = null;
            UpdateActions();
            if (id is not { } run) return;
            try
            {
                var element = await Task.Run(() => JsonSerializer.SerializeToElement(LocalTraining.Evidence(Store.Root, run)));
                if (selected == run)
                {
                    ShowEvidence(run, element); shownRun = run;
                    if (evidence.Visibility != Visibility.Visible) { evidence.Visibility = Visibility.Visible; Fluent.Appear(evidence, 6); }
                }
            }
            catch (Exception e) { notify(e.Message, InfoBarSeverity.Error); }
            finally { if (selected == run) evidenceLoading.Visibility = Visibility.Collapsed; }
            UpdateActions();
        }

        void Refresh()
        {
            updating = true;
            try
            {
                var runs = TrainingRuns();
                history.ItemsSource = runs;
                history.SelectedItem = runs.FirstOrDefault(x => x.Id == selected);
                history.Visibility = runs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                empty.Title.Text = runs.Count == 0 ? "No training runs yet" : "Choose a run";
                empty.Body.Text = runs.Count == 0
                    ? "Fill in the inputs, then choose Check & prepare. A prepared run can be started, stopped at a checkpoint and resumed later."
                    : "Select a retained run above to see its state, loss and checkpoints, or prepare a new one.";
            }
            finally { updating = false; }
        }

        history.SelectionChanged += async (_, _) =>
        {
            if (updating) return;
            selected = (history.SelectedItem as TrainingRunItem)?.Id;
            celebrate.Visibility = Visibility.Collapsed;
            await ShowRun(selected);
        };

        // Live progress from the engine. Evidence (chart, checkpoints) is re-read at most every 1.5 s.
        void Report(string text, int? done, int? total) => DispatcherQueue.TryEnqueue(async () =>
        {
            liveLine.Text = text + (total.HasValue ? $" · {done}/{total}" : "");
            status.Text = "Training lab · " + liveLine.Text;
            progress.Visibility = Visibility.Visible;
            progress.IsIndeterminate = !total.HasValue;
            if (total.HasValue) { progress.Maximum = total.Value; progress.Value = Math.Min(done ?? 0, total.Value); }
            if (running is { } id && selected == id && DateTimeOffset.UtcNow - lastLiveRefresh > TimeSpan.FromSeconds(1.5))
            {
                lastLiveRefresh = DateTimeOffset.UtcNow;
                try
                {
                    var element = await Task.Run(() => JsonSerializer.SerializeToElement(LocalTraining.Evidence(Store.Root, id)));
                    if (selected == id) ShowEvidence(id, element);
                }
                catch { /* The next status change retries; the final read after the run reports errors. */ }
            }
        });

        string script = Path.Combine(AppContext.BaseDirectory, "model-runtime", "Train-LocalAdapter.py");

        prepare.Click += async (_, _) =>
        {
            Guid id = Guid.NewGuid();
            bool ready = false;
            await GymRun(notify, async ct =>
            {
                var folder = RunFolder(id);
                var spec = new LocalTrainingSpec(basis.Text, bundle.Text, folder, GymInteger(steps, "Optimizer steps"), GymInteger(context, "Sequence length"), GymInteger(rank, "LoRA rank"), Device: (string)device.SelectedItem);
                var runtime = python.Text;
                celebrate.Visibility = Visibility.Collapsed;
                running = id;
                UpdateActions();
                try
                {
                    await LocalTraining.Run(runtime, script, spec, true, Report, ct);
                    GymFiles.Atomic(Path.Combine(folder, "python.json"), runtime);
                    ready = true;
                }
                finally { running = null; }
            });
            // Show the run whether or not the check passed, as long as the engine created its folder.
            if (Directory.Exists(RunFolder(id))) selected = id;
            Refresh();
            await ShowRun(selected);
            if (ready) notify("Ready. Review the configuration, then start training.", InfoBarSeverity.Success);
        };

        start.Click += async (_, _) =>
        {
            if (selected is not { } id) { notify("Prepare or choose a retained run first.", InfoBarSeverity.Warning); return; }
            bool completed = false;
            await GymRun(notify, async ct =>
            {
                var folder = RunFolder(id);
                var spec = JsonSerializer.Deserialize<LocalTrainingSpec>(File.ReadAllText(Path.Combine(folder, "config.json")))!;
                if (Path.GetFullPath(spec.Output) != Path.GetFullPath(folder)) throw new InvalidDataException("Training output identity changed.");
                var runtime = JsonSerializer.Deserialize<string>(File.ReadAllText(Path.Combine(folder, "python.json")))!;
                celebrate.Visibility = Visibility.Collapsed;
                running = id;
                UpdateActions();
                try
                {
                    await LocalTraining.Run(runtime, script, spec, false, Report, ct);
                    completed = true;
                }
                finally { running = null; }
            });
            Refresh();
            await ShowRun(selected);
            // Stop at checkpoint ends the run as a cancellation; say that it paused and can resume, not just "Cancelled".
            if (!completed && TrainingRunState(RunFolder(id)) == "Interrupted")
                notify("Training stopped at a checkpoint. Committed checkpoints are kept; choose Train / resume to continue from the latest one.", InfoBarSeverity.Informational);
            if (completed)
            {
                notify("Training completed. The adapter, checkpoints and metrics are retained in the evidence folder.", InfoBarSeverity.Success);
                celebrate.Visibility = Visibility.Visible;
                PlayReaction(celebrate, "that-worked", animate: true);
            }
        };

        serve.Click += async (_, _) =>
        {
            if (selected is not { } id) { notify("Choose a completed training run.", InfoBarSeverity.Warning); return; }
            serving = true;
            await GymRun(notify, async ct =>
            {
                // Device and loaded context come from the run itself, not from the settings for a new run.
                var served = await LocalTraining.StartAdapter(Store.Root, id, modelServing, Report, ct);
                var profiles = Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? [];
                profiles.RemoveAll(x => x.Id == served.Profile.Id);
                profiles.Add(served.Profile);
                Store.Write("ai-connections.json", profiles);
                refreshAiConnectionList?.Invoke();
                notify($"Adapter running at {served.Profile.Endpoint} with a {served.Options?.ContextTokens:N0}-token context. Open Compare models to try it. Stop it in Models & connections.", InfoBarSeverity.Success);
            });
            serving = false;
            UpdateActions();
            progress.IsIndeterminate = false;
            progress.Visibility = Visibility.Collapsed;
            await ShowRun(selected);
        };

        _ = ShowRun(null);
        try
        {
            Refresh();
            // Start on the most recently touched run; the selection handler loads its evidence.
            if (history.Items.Count > 0) history.SelectedIndex = 0;
        }
        catch (Exception e) { notify(e.Message, InfoBarSeverity.Error); }

        var left = new StackPanel { Spacing = 16, Children = { inputs, parameters } };
        var body = TwoPane(left, runCard, 5, 8, 980);
        var page = new StackPanel { Spacing = 14, Margin = new Thickness(0, 0, 4, 16), Children = { noticeView, body } };
        return PageLayout("Training lab", "Prepare, train, resume, and serve local LoRA adapters with retained evidence.", VerticalScroll(page));
    }
}

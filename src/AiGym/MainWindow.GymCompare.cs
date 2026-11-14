using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AiGym;

// Compare models: the same held-out prompts go to two saved connections. Both answers, the client
// latency and the exact-match observation are retained as evidence and shown side by side.
public sealed partial class MainWindow
{
    /// <summary>
    /// One observed response. Property names and order match the evidence file written since the
    /// first release, so saved comparisons stay readable in both directions.
    /// </summary>
    sealed record CompareRow(string EntityHash, string Model, string Expected, string Output, string? FinishReason, long Milliseconds, bool ExactMatch);

    sealed record SavedComparison(string Path, string State, string Baseline, string Candidate, DateTime Modified)
    {
        public override string ToString() => $"{Modified.ToLocalTime():g} · {State} · {Baseline} vs {Candidate}";
    }

    /// <summary>Output limit per held-out answer; lowered per request when the prompt leaves less room in the loaded context.</summary>
    const int CompareOutputTokens = 512;

    const string CompareQualification ="Up to ten held-out examples; displayed output, client latency, and exact match are observations, not a general quality score.";

    FrameworkElement BuildGymComparison()
    {
        var (noticeView, notify) = GymNotice();
        var baseline = new ComboBox { Header = "Baseline", DisplayMemberPath = "DisplayName", HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Choose a saved connection" };
        var candidate = new ComboBox { Header = "Candidate", DisplayMemberPath = "DisplayName", HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Choose a saved connection" };
        var datasets = new ComboBox { Header = "Held-out dataset", DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Choose a saved dataset" };
        var datasetFact = Muted("", 12);
        var compare = Button("Compare held-out responses", (_, _) => { }, true);
        var cancel = Button("Cancel", (_, _) => gymOperation?.Cancel());
        var refresh = Button("Refresh choices", (_, _) => { });
        var reason = Muted("", 12);
        var setupLinks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
        var progress = BrandControl(new ProgressBar { Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed });
        AutomationProperties.SetName(progress, "Comparison progress");
        var progressText = Muted("", 12);
        progressText.Visibility = Visibility.Collapsed;
        bool comparing = false;
        int connectionCount = 0, datasetCount = 0;

        var selection = StepCard("Step 1", "Set up the comparison", "Choose two distinct saved connections and a dataset. Up to ten held-out conversations are sent to both endpoints; expected responses are withheld from requests.",
            FormGrid(baseline, candidate), datasets, datasetFact,
            new ScrollViewer { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { compare, cancel, refresh } }, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled },
            reason, setupLinks, progress, progressText);

        // Results.
        var saved = new ComboBox { Header = "Saved comparison", HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Open retained evidence" };
        var statePill = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        var resultTitle = Heading("", 17);
        var baseMatch = StatTile("Baseline exact matches");
        var baseLatency = StatTile("Baseline median latency");
        var candMatch = StatTile("Candidate exact matches");
        var candLatency = StatTile("Candidate median latency");
        var tiles = FlowGrid(170, baseMatch.View, baseLatency.View, candMatch.View, candLatency.View);
        var chart = new ChartView(new ChartDefinition(Guid.NewGuid(), Kind: "Bar", X: "example", Values: ["baseline", "candidate"], Aggregate: "Sum", Axis: "Category", Sort: "Result order", Title: "Client latency per held-out example", Unit: "ms", Palette: "Gym"))
        {
            Telemetry = true, Compact = true, Height = 280, WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this)
        };
        var chartCard = Surface(chart, "GymSurfaceBrush", 12, 6);
        var examples = new StackPanel { Spacing = 12 };
        var pathHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var qualification = Muted(CompareQualification, 12);
        var results = new StackPanel
        {
            Spacing = 14,
            Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { statePill, resultTitle } }, tiles, chartCard, qualification, pathHost, examples }
        };
        var empty = EmptyState("let-me-think", "No comparison open", "Run a comparison, or open retained evidence from the list above. Nothing is shown until responses are actually observed.");
        var plainNotify = notify;
        notify = (message, severity) =>
        {
            plainNotify(message, severity);
            if (severity == InfoBarSeverity.Error) PlayReaction(empty.Art, "try-again");
        };
        var evidence = StepCard("Step 2", "Read the evidence", "Responses and observed client latency for each held-out example. Exact matches are not a general quality score.", saved, results, empty.View);
        results.Visibility = Visibility.Collapsed;

        void UpdateActions()
        {
            bool busy = gymOperation != null;
            string why = busy ? (comparing ? "Comparison in progress." : "Another lab operation is active.")
                : connectionCount < 2 ? "Save at least two model connections to compare."
                : datasetCount == 0 ? "Import a dataset first; its held-out examples are the prompts."
                : baseline.SelectedItem == null || candidate.SelectedItem == null || datasets.SelectedItem == null ? "Choose a baseline, a candidate and a dataset."
                : (baseline.SelectedItem as AiEndpointProfile)?.Id == (candidate.SelectedItem as AiEndpointProfile)?.Id ? "Choose distinct baseline and candidate connections."
                : "";
            compare.IsEnabled = why.Length == 0;
            cancel.IsEnabled = busy && comparing;
            refresh.IsEnabled = !busy;
            saved.IsEnabled = !busy;
            reason.Text = why;
            reason.Visibility = why.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            setupLinks.Visibility = !busy && (connectionCount < 2 || datasetCount == 0) ? Visibility.Visible : Visibility.Collapsed;
        }
        gymBusyListeners.Add(UpdateActions);
        setupLinks.Children.Add(Button("Open Models & connections", (_, _) => Navigate("Models")));
        setupLinks.Children.Add(Button("Open Datasets", (_, _) => Navigate("Datasets")));

        void DatasetFact()
        {
            datasetFact.Visibility = datasets.SelectedItem is GymDataset ? Visibility.Visible : Visibility.Collapsed;
            if (datasets.SelectedItem is not GymDataset data) { datasetFact.Text = ""; return; }
            var held = GymDatasets.HeldOut(data);
            int count = data.Examples.Count(x => held.Contains(x.entity));
            datasetFact.Text = $"{count:N0} held-out examples from {held.Count:N0} entities · {Math.Min(10, count):N0} will be sent to each model.";
        }

        // Rows arrive in pairs: baseline first, then candidate, for each held-out example.
        void ShowRows(string state, string baselineName, string candidateName, IReadOnlyList<CompareRow> rows, string? path)
        {
            results.Visibility = Visibility.Visible;
            empty.View.Visibility = Visibility.Collapsed;
            statePill.Content = Pill(state, StateTone(state));
            resultTitle.Text = $"{baselineName}  vs  {candidateName}";
            var first = rows.Where((_, i) => i % 2 == 0).ToArray();
            var second = rows.Where((_, i) => i % 2 == 1).ToArray();
            static string Median(CompareRow[] items)
            {
                if (items.Length == 0) return "—";
                var ordered = items.Select(x => x.Milliseconds).Order().ToArray();
                double median = ordered.Length % 2 == 1 ? ordered[ordered.Length / 2] : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2.0;
                return $"{median:N0} ms";
            }
            baseMatch.Set(first.Length == 0 ? "—" : $"{first.Count(x => x.ExactMatch)} / {first.Length}", baselineName);
            candMatch.Set(second.Length == 0 ? "—" : $"{second.Count(x => x.ExactMatch)} / {second.Length}", candidateName);
            baseLatency.Set(Median(first), $"{first.Length} responses");
            candLatency.Set(Median(second), $"{second.Length} responses");

            var table = new DataTable();
            table.Columns.Add("example", typeof(string));
            table.Columns.Add("baseline", typeof(double));
            table.Columns.Add("candidate", typeof(double));
            for (int i = 0; i < first.Length; i++) table.Rows.Add((i + 1).ToString(), (double)first[i].Milliseconds, i < second.Length ? second[i].Milliseconds : DBNull.Value);
            chartCard.Visibility = first.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            chart.CaptionOverride = $"{rows.Count:N0} observed responses · baseline {baselineName} · candidate {candidateName} · latency is measured by this client, including streaming";
            chart.Bind(table, false, table.Rows.Count);

            examples.Children.Clear();
            for (int i = 0; i < first.Length; i++)
            {
                FrameworkElement Answer(string role, string model, CompareRow? row)
                {
                    var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    var who = Label(role + " · " + model, 13);
                    who.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                    var panel = new StackPanel { Spacing = 8, Children = { who } };
                    if (row == null) { panel.Children.Add(Muted("No response observed yet.", 13)); return Surface(panel, "GymSurfaceAltBrush", 12, 14, border: false); }
                    head.Children.Add(Pill($"{row.Milliseconds:N0} ms"));
                    head.Children.Add(row.ExactMatch ? Pill("Exact match", GymTone.Accent) : Pill("Differs from expected"));
                    if (!string.IsNullOrEmpty(row.FinishReason)) head.Children.Add(Pill("finish · " + row.FinishReason, row.FinishReason == "stop" ? GymTone.Neutral : GymTone.Warn));
                    panel.Children.Add(new ScrollViewer { Content = head, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
                    var output = Label(row.Output.Length == 0 ? "(empty response)" : row.Output, 13.5);
                    output.IsTextSelectionEnabled = true;
                    panel.Children.Add(new ScrollViewer { Content = output, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
                    return Surface(panel, "GymSurfaceAltBrush", 12, 14, border: false);
                }
                var expected = Label(first[i].Expected, 13.5);
                expected.IsTextSelectionEnabled = true;
                var title = Label($"Example {i + 1}", 14);
                title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                var entity = Muted("entity " + ShortHash(first[i].EntityHash), 12);
                ToolTipService.SetToolTip(entity, "Entity SHA-256 · " + first[i].EntityHash);
                entity.VerticalAlignment = VerticalAlignment.Center;
                var card = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { title, entity } },
                        new StackPanel { Spacing = 3, Children = { Muted("Expected response (withheld from both requests)", 12), new ScrollViewer { Content = expected, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } } },
                        FormGrid(560, Answer("Baseline", baselineName, first[i]), Answer("Candidate", candidateName, i < second.Length ? second[i] : null))
                    }
                };
                examples.Children.Add(Surface(card, "GymSurfaceBrush", 14, 16));
            }
            // Before the first response arrives, a skeleton in the shape of an example card shows that answers are on their way.
            if (first.Length == 0 && state == "Running")
                examples.Children.Add(Surface(new StackPanel { Spacing = 14, Children = { Fluent.Skeleton(.25, .9, .6), FormGrid(560, Surface(Fluent.Skeleton(.5, .95, .8), "GymSurfaceAltBrush", 12, 14, border: false), Surface(Fluent.Skeleton(.5, .9, .85), "GymSurfaceAltBrush", 12, 14, border: false)) } }, "GymSurfaceBrush", 14, 16));
            pathHost.Content = path == null ? null : ValueRow("Evidence file", path);
        }

        // Evidence records the server's model ID, which for managed models is an internal alias such as
        // "aigym-chat-…". Show the saved connection's name when that connection still exists.
        List<AiEndpointProfile> named = [];
        string Who(JsonElement side)
        {
            string model = side.GetProperty("Model").GetString() ?? "";
            return side.TryGetProperty("Id", out var id) && id.TryGetGuid(out var guid) && named.FirstOrDefault(x => x.Id == guid && x.Model == model) is { } profile ? profile.Name : model;
        }

        List<SavedComparison> ListSaved()
        {
            named = Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? [];
            var folder = Path.Combine(Store.Root, "comparisons");
            var items = new List<SavedComparison>();
            if (!Directory.Exists(folder)) return items;
            foreach (var file in Directory.GetFiles(folder, "*.json"))
            {
                try
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(file));
                    var r = json.RootElement;
                    items.Add(new(file, r.GetProperty("State").GetString() ?? "Unknown", Who(r.GetProperty("Baseline")), Who(r.GetProperty("Candidate")), File.GetLastWriteTimeUtc(file)));
                }
                catch { /* Not a comparison evidence file; leave it untouched and unlisted. */ }
            }
            return items.OrderByDescending(x => x.Modified).ToList();
        }

        bool updating = false;
        void Refresh()
        {
            var profiles = Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? [];
            var keepA = (baseline.SelectedItem as AiEndpointProfile)?.Id;
            var keepB = (candidate.SelectedItem as AiEndpointProfile)?.Id;
            var keepData = (datasets.SelectedItem as GymDataset)?.Id;
            baseline.ItemsSource = profiles;
            candidate.ItemsSource = profiles.ToList();
            baseline.SelectedItem = profiles.FirstOrDefault(x => x.Id == keepA);
            candidate.SelectedItem = profiles.FirstOrDefault(x => x.Id == keepB);
            var sets = GymDatasets.List(Store.Root).ToArray();
            datasets.ItemsSource = sets;
            datasets.SelectedItem = sets.FirstOrDefault(x => x.Id == keepData);
            connectionCount = profiles.Count;
            datasetCount = sets.Length;
            updating = true;
            try
            {
                var keepSaved = (saved.SelectedItem as SavedComparison)?.Path;
                var list = ListSaved();
                saved.ItemsSource = list;
                saved.SelectedItem = list.FirstOrDefault(x => x.Path == keepSaved);
                saved.Visibility = list.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            finally { updating = false; }
            DatasetFact();
            UpdateActions();
        }
        void SafeRefresh()
        {
            try { Refresh(); }
            catch (Exception e) { notify(e.Message, InfoBarSeverity.Error); }
        }
        refreshGymDatasets += SafeRefresh;
        // "Use in Compare" on the model pages lands here with that connection as the candidate.
        selectCompareCandidate = id =>
        {
            var match = (candidate.ItemsSource as IEnumerable<AiEndpointProfile>)?.FirstOrDefault(x => x.Id == id);
            // A listed entry can be older than the saved connection (a restarted model gets a new port); only the current one counts.
            AiEndpointProfile? current;
            try { current = (Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? []).FirstOrDefault(x => x.Id == id); }
            catch (Exception e) { notify("Saved connections could not be read: " + e.Message, InfoBarSeverity.Error); return; }
            if (match != null && (current == null || current.Endpoint != match.Endpoint || current.Model != match.Model)) match = null;
            // While a lab operation runs the pickers are not refreshed, so a new connection may not be listed yet.
            // A connection that is already listed can still be preselected, unless a comparison is using the pickers.
            if (gymOperation != null && (comparing || match == null))
            {
                notify(comparing ? "A comparison is running, so the model was not preselected. Choose it as the candidate when the comparison finishes."
                    : "A lab operation is running, so the model was not preselected. When it finishes, choose Refresh choices and select the model as the candidate.", InfoBarSeverity.Warning);
                return;
            }
            if (match == null) { notify("That model's connection is not saved, so it could not be preselected. Start it again from Models & connections.", InfoBarSeverity.Warning); return; }
            candidate.SelectedItem = match;
            UpdateActions();
        };
        refresh.Click += (_, _) => SafeRefresh();
        baseline.SelectionChanged += (_, _) => UpdateActions();
        candidate.SelectionChanged += (_, _) => UpdateActions();
        datasets.SelectionChanged += (_, _) => { DatasetFact(); UpdateActions(); };

        saved.SelectionChanged += (_, _) =>
        {
            if (updating || saved.SelectedItem is not SavedComparison item) return;
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(item.Path));
                var rows = json.RootElement.GetProperty("Rows").Deserialize<List<CompareRow>>() ?? [];
                ShowRows(item.State, item.Baseline, item.Candidate, rows, item.Path);
                Fluent.Appear(results, 6);
            }
            catch (Exception e) { notify("This evidence file could not be read: " + e.Message, InfoBarSeverity.Error); }
        };

        compare.Click += async (_, _) =>
        {
            bool completed = false;
            string? written = null;
            comparing = true;
            await GymRun(notify, async ct =>
            {
                if (baseline.SelectedItem is not AiEndpointProfile a || candidate.SelectedItem is not AiEndpointProfile b || datasets.SelectedItem is not GymDataset data) throw new ArgumentException("Choose two models and a dataset.");
                if (a.Id == b.Id) throw new ArgumentException("Choose distinct baseline and candidate connections.");
                var held = GymDatasets.HeldOut(data);
                var prompts = data.Examples.Where(x => held.Contains(x.entity)).Take(10).ToArray();
                var id = Guid.NewGuid();
                var path = Path.Combine(Store.Root, "comparisons", id + ".json");
                written = path;
                var rows = new List<CompareRow>();
                void Save(string state) => GymFiles.Atomic(path, new { Id = id, State = state, Dataset = data.Id, data.SourceHash, Baseline = new { a.Id, a.Model, a.Endpoint }, Candidate = new { b.Id, b.Model, b.Endpoint }, Rows = rows, Qualification = CompareQualification });
                void Progress(string state)
                {
                    progress.Visibility = Visibility.Visible;
                    progress.Maximum = Math.Max(1, prompts.Length * 2);
                    progress.Value = rows.Count;
                    progressText.Text = $"Observed {rows.Count}/{prompts.Length * 2} responses";
                    progressText.Visibility = Visibility.Visible;
                    status.Text = "Compare models · " + progressText.Text;
                    ShowRows(state, a.Name, b.Name, rows, path);
                }
                Save("Running");
                Progress("Running");
                try
                {
                    foreach (var example in prompts)
                        foreach (var profile in new[] { a, b })
                        {
                            ct.ThrowIfCancellationRequested();
                            var text = new StringBuilder();
                            using var client = new LocalAiClient();
                            var key = aiSessionKeys.GetValueOrDefault(profile.Id) ?? Store.Unprotect(profile.Secret);
                            var request = example.messages[..^1].Select(m => new AiMessage(m.role, m.content)).ToArray();
                            // Measured before the clock starts, so latency covers only the answer.
                            int output = await client.FitOutput(profile, key, request, CompareOutputTokens, ct);
                            var timer = Stopwatch.StartNew();
                            var answer = await client.StreamChat(profile, key, request, output, part => text.Append(part), ct);
                            if (answer.ObservedModel is { } observed && observed != profile.Model) throw new InvalidDataException("Response model identity differs from the selected model.");
                            rows.Add(new(GymFiles.Hash(example.entity), profile.Model, example.messages[^1].content, text.ToString(), answer.FinishReason, timer.ElapsedMilliseconds, text.ToString().Trim() == example.messages[^1].content.Trim()));
                            Save("Running");
                            Progress("Running");
                        }
                    Save("Completed");
                    Progress("Completed");
                    completed = true;
                }
                catch
                {
                    string final = ct.IsCancellationRequested ? "Cancelled" : "Failed";
                    Save(final);
                    ShowRows(final, a.Name, b.Name, rows, path);
                    throw;
                }
            });
            comparing = false;
            progress.Visibility = Visibility.Collapsed;
            if (completed) { progressText.Visibility = Visibility.Collapsed; notify("Comparison completed. Both sets of responses are saved with the evidence file below.", InfoBarSeverity.Success); }
            SafeRefresh();
            // The results on screen are this run's; the saved-evidence picker must name the same file, not an older one.
            if (written != null)
            {
                updating = true;
                try { saved.SelectedItem = (saved.ItemsSource as IEnumerable<SavedComparison>)?.FirstOrDefault(x => x.Path == written); }
                finally { updating = false; }
            }
        };

        SafeRefresh();
        // Open the latest retained evidence, if any, instead of an empty panel.
        if (saved.Items.Count > 0) saved.SelectedIndex = 0;
        var page = new StackPanel { Spacing = 14, Margin = new Thickness(0, 0, 4, 16), Children = { noticeView, selection, evidence } };
        return PageLayout("Compare models", "Use the same held-out prompts and retain both responses with measured client latency.", VerticalScroll(page));
    }
}

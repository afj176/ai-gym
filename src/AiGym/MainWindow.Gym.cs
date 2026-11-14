using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace AiGym;

// Shared lab plumbing plus the Datasets page. Training, comparison and the agent lab live in
// MainWindow.GymTraining.cs, MainWindow.GymCompare.cs and MainWindow.GymLab.cs.
public sealed partial class MainWindow
{
    CancellationTokenSource? gymOperation;
    Action? refreshGymDatasets;
    // Pages register here to enable or disable their actions while a lab operation owns the window.
    readonly List<Action> gymBusyListeners = [];

    void GymBusyChanged()
    {
        foreach (var listener in gymBusyListeners) listener();
    }

    /// <summary>
    /// Runs one lab operation at a time. Failures and cancellation are reported through
    /// <paramref name="notify"/>; nothing is reported as finished unless the action returned.
    /// </summary>
    async Task GymRun(Action<string, InfoBarSeverity> notify, Func<CancellationToken, Task> action)
    {
        if (gymOperation != null) { notify("Finish or cancel the current lab operation first.", InfoBarSeverity.Warning); return; }
        using var operation = new CancellationTokenSource();
        gymOperation = operation;
        GymBusyChanged();
        try { await action(operation.Token); }
        catch (OperationCanceledException) { notify("Cancelled. Retained evidence and committed checkpoints remain available.", InfoBarSeverity.Informational); }
        catch (Exception error) { notify(error.Message, InfoBarSeverity.Error); }
        finally { gymOperation = null; GymBusyChanged(); }
    }

    async Task GymPick(TextBox box, bool folder, string extension = ".jsonl")
    {
        if (folder)
        {
            var value = await new FolderPicker(AppWindow.Id).PickSingleFolderAsync();
            if (value != null) box.Text = value.Path;
        }
        else
        {
            var picker = new FileOpenPicker(AppWindow.Id);
            picker.FileTypeFilter.Add(extension);
            var value = await picker.PickSingleFileAsync();
            if (value != null) box.Text = value.Path;
        }
    }

    static int GymInteger(NumberBox field, string name)
    {
        if (!double.IsFinite(field.Value) || field.Value != Math.Truncate(field.Value) || field.Value < field.Minimum || field.Value > field.Maximum) throw new ArgumentException(name + " must be a whole number within the displayed bounds.");
        return checked((int)field.Value);
    }

    FrameworkElement BuildGymDatasets()
    {
        GymDataset[] saved = [];
        GymDataset? current = null;
        HashSet<string> heldOut = [];
        GymExample[] shown = [];
        int position = 0;
        bool exporting = false;

        var (noticeView, notify) = GymNotice();

        // Left: saved datasets and the import target.
        var list = BrandControl(new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 420 });
        AutomationProperties.SetName(list, "Saved datasets");
        var listEmpty = Muted("No datasets saved yet.", 13);
        var import = Button("Import JSONL", (_, _) => { }, true);
        var dropHint = Muted("Or drop a .jsonl file anywhere on this page.", 12);
        var format = Muted("Each line needs an entity and role/content messages ending in an assistant response. Entity groups stay together in the deterministic 80/20 split.", 12);
        var listCard = StepCard("Step 1", "Choose a dataset", "", list, listEmpty, new StackPanel { Spacing = 6, Children = { import, dropHint, format } });

        // Right: summary, split, examples and export for the selected dataset.
        var name = Heading("", 22);
        var imported = Muted("", 13);
        var examplesTile = StatTile("Examples");
        var entitiesTile = StatTile("Entities");
        var trainTile = StatTile("Training examples");
        var heldTile = StatTile("Held-out examples");
        var tiles = FlowGrid(150, examplesTile.View, entitiesTile.View, trainTile.View, heldTile.View);
        var splitHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var splitNote = Muted("", 12);
        var hashHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var summaryCard = Surface(new StackPanel
        {
            Spacing = 14,
            Children = { new StackPanel { Spacing = 2, Children = { Eyebrow("Selected dataset"), name, imported } }, tiles, new StackPanel { Spacing = 6, Children = { Label("Train / held-out split", 13), splitHost, splitNote } }, hashHost }
        });

        // Example browser: one conversation at a time, as chat bubbles.
        var filter = new ComboBox { ItemsSource = new[] { "All examples", "Training only", "Held out only" }, SelectedIndex = 0, MinWidth = 150 };
        AutomationProperties.SetName(filter, "Filter examples by split");
        var previous = IconButton("", "Previous example", (_, _) => { });
        var next = IconButton("", "Next example", (_, _) => { });
        var counter = Label("", 13);
        counter.VerticalAlignment = VerticalAlignment.Center;
        var entity = Muted("", 12);
        entity.TextTrimming = TextTrimming.CharacterEllipsis;
        entity.TextWrapping = TextWrapping.NoWrap;
        var splitPill = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        var bubbles = new StackPanel { Spacing = 10 };
        var bubbleScroll = new ScrollViewer { Content = bubbles, MaxHeight = 440, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 14, 0) };
        var pager = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { filter, previous, counter, next } };
        var examplesCard = StepCard("Step 2", "Review the examples", "Page through every conversation. Held-out entities never appear in the training split.",
            new ScrollViewer { Content = pager, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { splitPill, entity } },
            bubbleScroll);

        // Export keeps the review-before-export safeguard: the box must be ticked for this dataset.
        var approved = BrandControl(new CheckBox { Content = "I reviewed these examples and approve this dataset for training export" });
        var export = Button("Export training bundle", (_, _) => { }, true);
        var exportReason = Muted("", 12);
        var exportResult = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
        var celebrate = Mascot("that-worked", 96);
        celebrate.Visibility = Visibility.Collapsed;
        var exportRow = new Grid { ColumnSpacing = 16 };
        exportRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        exportRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        exportRow.Children.Add(new StackPanel { Spacing = 10, Children = { approved, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { export, exportReason } }, exportResult } });
        Grid.SetColumn(celebrate, 1);
        exportRow.Children.Add(celebrate);
        exportReason.VerticalAlignment = VerticalAlignment.Center;
        var exportCard = StepCard("Step 3", "Approve and export", "Export writes train.jsonl, evaluation.jsonl and a manifest into a new folder. Existing bundles are never overwritten.", exportRow);

        var detail = new StackPanel { Spacing = 16, Children = { summaryCard, examplesCard, exportCard } };
        var empty = EmptyState("im-listening", "Bring some conversations", "Import a JSONL file to see its examples, the train / held-out split and its fingerprint here.");
        var sample = MonoBlock("{\"entity\":\"customer-17\",\"messages\":[{\"role\":\"user\",\"content\":\"…\"},{\"role\":\"assistant\",\"content\":\"…\"}]}");
        sample.MaxWidth = 620;
        var sampleCaption = Muted("One line of the expected format:", 12);
        sampleCaption.HorizontalAlignment = HorizontalAlignment.Center;
        var emptyCard = Surface(new StackPanel { Spacing = 4, Children = { empty.View, sampleCaption, sample } });
        var right = new Grid { Children = { detail, emptyCard } };

        void UpdateActions()
        {
            bool busy = gymOperation != null || exporting;
            import.IsEnabled = !busy;
            export.IsEnabled = current != null && approved.IsChecked == true && !busy;
            exportReason.Text = current == null ? "Select a dataset first."
                : exporting ? "Exporting…"
                : approved.IsChecked != true ? "Tick the review box to enable export."
                : "";
        }
        gymBusyListeners.Add(UpdateActions);

        void ShowExample()
        {
            bubbles.Children.Clear();
            previous.IsEnabled = position > 0;
            next.IsEnabled = position < shown.Length - 1;
            if (shown.Length == 0)
            {
                counter.Text = "No examples in this split";
                entity.Text = "";
                splitPill.Content = null;
                return;
            }
            var example = shown[position];
            counter.Text = $"{position + 1:N0} of {shown.Length:N0}";
            entity.Text = "Entity · " + example.entity;
            ToolTipService.SetToolTip(entity, example.entity);
            splitPill.Content = heldOut.Contains(example.entity) ? Pill("Held out", GymTone.Warn) : Pill("Training", GymTone.Accent);
            foreach (var message in example.messages) bubbles.Children.Add(Bubble(message.role, message.content));
            bubbleScroll.ChangeView(null, 0, null, true);
            // The next conversation settles into place instead of popping.
            Fluent.Appear(bubbles, 6);
        }

        void ApplyFilter()
        {
            if (current == null) { shown = []; return; }
            shown = filter.SelectedIndex switch
            {
                1 => current.Examples.Where(x => !heldOut.Contains(x.entity)).ToArray(),
                2 => current.Examples.Where(x => heldOut.Contains(x.entity)).ToArray(),
                _ => current.Examples
            };
            position = 0;
            ShowExample();
        }

        void ShowDataset(GymDataset? data)
        {
            current = data;
            // Approval never carries over from one dataset to another.
            approved.IsChecked = false;
            exportResult.Visibility = Visibility.Collapsed;
            celebrate.Visibility = Visibility.Collapsed;
            detail.Visibility = data == null ? Visibility.Collapsed : Visibility.Visible;
            emptyCard.Visibility = data == null ? Visibility.Visible : Visibility.Collapsed;
            if (data == null)
            {
                empty.Title.Text = saved.Length == 0 ? "Bring some conversations" : "Choose a dataset";
                empty.Body.Text = saved.Length == 0
                    ? "Import a JSONL file to see its examples, the train / held-out split and its fingerprint here."
                    : "Select a saved dataset on the left to review its examples and split.";
                UpdateActions();
                return;
            }
            heldOut = GymDatasets.HeldOut(data);
            int entities = data.Examples.Select(x => x.entity).Distinct(StringComparer.Ordinal).Count();
            int heldExamples = data.Examples.Count(x => heldOut.Contains(x.entity));
            name.Text = data.Name;
            imported.Text = $"Imported {data.Imported.LocalDateTime:g}";
            examplesTile.Set(data.Examples.Length.ToString("N0"), "");
            entitiesTile.Set(entities.ToString("N0"), $"{heldOut.Count:N0} held out");
            trainTile.Set((data.Examples.Length - heldExamples).ToString("N0"), "");
            heldTile.Set(heldExamples.ToString("N0"), "");
            splitHost.Content = SplitBar(data.Examples.Length - heldExamples, heldExamples);
            splitNote.Text = $"{heldOut.Count:N0} of {entities:N0} entities are held out: the first ceil(20%) in SHA-256 order. An entity's examples always stay on one side.";
            hashHost.Content = ValueRow("Source SHA-256", data.SourceHash);
            filter.SelectedIndex = 0;
            ApplyFilter();
            UpdateActions();
        }

        void Refresh()
        {
            var keep = current?.Id;
            var (valid, skipped) = GymDatasets.Load(Store.Root);
            saved = valid.OrderByDescending(x => x.Imported).ToArray();
            if (skipped.Length > 0)
                notify($"Skipped {skipped.Length} saved dataset file{(skipped.Length == 1 ? "" : "s")} that could not be read. {(skipped.Length == 1 ? "It is" : "They are")} kept unchanged in {Path.Combine(Store.Root, "datasets")}:\n"
                    + string.Join("\n", skipped.Select(x => $"{x.File} · {x.Reason}")), InfoBarSeverity.Warning);
            list.Items.Clear();
            foreach (var data in saved)
            {
                var title = Label(data.Name, 14);
                title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                title.TextWrapping = TextWrapping.NoWrap;
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                var item = new ListViewItem
                {
                    Tag = data,
                    Padding = new Thickness(12, 8, 12, 8),
                    Content = new StackPanel { Spacing = 1, Children = { title, Muted($"{data.Examples.Length:N0} examples · {data.Imported.LocalDateTime:d}", 12) } }
                };
                AutomationProperties.SetName(item, $"{data.Name}, {data.Examples.Length} examples");
                list.Items.Add(item);
            }
            list.Visibility = saved.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            listEmpty.Visibility = saved.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var match = list.Items.OfType<ListViewItem>().FirstOrDefault(x => ((GymDataset)x.Tag).Id == keep);
            list.SelectedItem = match;
            if (match == null) ShowDataset(null);
        }

        async Task ImportFrom(string path)
        {
            if (gymOperation != null || exporting) { notify("Finish or cancel the current lab operation first.", InfoBarSeverity.Warning); return; }
            try
            {
                var data = await Task.Run(() => GymDatasets.Import(path));
                GymDatasets.Save(Store.Root, data);
                Refresh();
                list.SelectedItem = list.Items.OfType<ListViewItem>().Single(x => ((GymDataset)x.Tag).Id == data.Id);
                refreshGymDatasets?.Invoke();
                PlayMascot(empty.Art, "im-listening");
                notify($"Imported {data.Examples.Length:N0} examples from {Path.GetFileName(path)}. Review them before approving export.", InfoBarSeverity.Success);
            }
            catch (Exception e)
            {
                notify(e.Message, InfoBarSeverity.Error);
                PlayMascot(empty.Art, "try-again");
            }
        }

        list.SelectionChanged += (_, _) => { if (list.SelectedItem is ListViewItem { Tag: GymDataset data }) { if (data.Id != current?.Id) { ShowDataset(data); Fluent.Appear(detail, 6); } } };
        // The title bar search lists saved datasets; choosing one opens it here.
        datasetSearchItems = () => saved.Select(data => ("Dataset · " + data.Name, (Action)(() =>
        {
            Navigate("Datasets");
            if (list.Items.OfType<ListViewItem>().FirstOrDefault(x => ((GymDataset)x.Tag).Id == data.Id) is { } item) { list.SelectedItem = item; list.ScrollIntoView(item); }
        })));
        filter.SelectionChanged += (_, _) => ApplyFilter();
        previous.Click += (_, _) => { if (position > 0) { position--; ShowExample(); } };
        next.Click += (_, _) => { if (position < shown.Length - 1) { position++; ShowExample(); } };
        approved.Checked += (_, _) => UpdateActions();
        approved.Unchecked += (_, _) => UpdateActions();
        import.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker(AppWindow.Id);
                picker.FileTypeFilter.Add(".jsonl");
                var file = await picker.PickSingleFileAsync();
                if (file != null) await ImportFrom(file.Path);
            }
            catch (Exception e) { notify(e.Message, InfoBarSeverity.Error); }
        };
        export.Click += async (_, _) =>
        {
            try
            {
                if (current is not { } data || approved.IsChecked != true) throw new InvalidOperationException("Select and review the complete dataset before export.");
                var parent = await new FolderPicker(AppWindow.Id).PickSingleFolderAsync();
                if (parent == null) return;
                var path = Path.Combine(parent.Path, "ai-gym-bundle-" + Guid.NewGuid().ToString("N")[..8]);
                exporting = true;
                UpdateActions();
                await Task.Run(() => GymDatasets.Export(data, path));
                exportResult.Content = ValueRow("Training bundle exported to", path, folder: true);
                exportResult.Visibility = Visibility.Visible;
                celebrate.Visibility = Visibility.Visible;
                PlayMascot(celebrate, "that-worked", animate: true);
                notify("Training bundle exported. Use this folder as the reviewed bundle in Training lab.", InfoBarSeverity.Success);
            }
            catch (Exception e) { notify(e.Message, InfoBarSeverity.Error); }
            finally { exporting = false; UpdateActions(); }
        };

        try
        {
            Refresh();
            // Open the most recently imported dataset so the page starts with something to review.
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }
        catch (Exception e) { notify(e.Message, InfoBarSeverity.Error); ShowDataset(null); }

        var body = TwoPane(listCard, right, 4, 9, 860);
        var page = new StackPanel { Spacing = 14, Margin = new Thickness(0, 0, 4, 16), Children = { noticeView, body } };
        var view = PageLayout("Datasets", "Review local conversations and export reproducible, entity-separated training bundles.", VerticalScroll(page));

        // Dropping a .jsonl file anywhere on the page imports it, same as the picker.
        // A transparent background makes the gaps between cards a drop target too.
        if (view is Panel surface) surface.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        view.AllowDrop = true;
        view.DragOver += (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Import as a dataset";
        };
        view.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var deferral = e.GetDeferral();
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var file = items.FirstOrDefault(x => x.Path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase));
                if (file == null) notify("Drop a .jsonl file. Other file types are not imported.", InfoBarSeverity.Warning);
                else await ImportFrom(file.Path);
            }
            catch (Exception error) { notify(error.Message, InfoBarSeverity.Error); }
            finally { deferral.Complete(); }
        };
        return view;
    }
}

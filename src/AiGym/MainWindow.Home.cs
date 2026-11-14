using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AiGym;

// Home: the practice loop (dataset, train, compare) with counts read from the profile folder.
// Every figure comes from disk or from state the other pages already loaded; nothing is estimated.
public sealed partial class MainWindow
{
    Action? refreshGymHome;

    /// <summary>What is actually saved in the profile right now. A null count means "could not be read".</summary>
    sealed record GymInventory(int? Datasets, int? Examples, int? TrainingRuns, int? CompletedRuns, int? Connections, int? Comparisons, int? Experiments, int SkippedDatasets = 0);

    GymInventory ReadGymInventory()
    {
        static int? Try(Func<int> read)
        {
            try { return read(); }
            catch { return null; }
        }
        string root = Store.Root;
        GymDataset[]? datasets = null;
        int skipped = 0;
        try { (datasets, var unreadable) = GymDatasets.Load(root); skipped = unreadable.Length; } catch { }

        string training = Path.Combine(root, "agent-labs", "training");
        string[] runs = [];
        try { if (Directory.Exists(training)) runs = Directory.GetDirectories(training).Where(x => Guid.TryParse(Path.GetFileName(x), out _)).ToArray(); } catch { }

        return new(
            datasets?.Length,
            datasets?.Sum(x => x.Examples.Length),
            runs.Length,
            Try(() => runs.Count(x => TrainingRunState(x) == "Completed")),
            Try(() => (Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? []).Count),
            Try(() => Directory.Exists(Path.Combine(root, "comparisons")) ? Directory.GetFiles(Path.Combine(root, "comparisons"), "*.json").Length : 0),
            Try(() => Directory.Exists(Path.Combine(root, "agent-labs")) ? Directory.GetFiles(Path.Combine(root, "agent-labs"), "*.json").Length : 0),
            skipped);
    }

    Action<Guid>? selectCompareCandidate;

    /// <summary>A served model is tried by comparing it against another: open Compare with it as the candidate.</summary>
    void OpenModelInCompare(Guid profile)
    {
        Navigate("Compare");
        selectCompareCandidate?.Invoke(profile);
    }

    /// <summary>Reads the state word the training runtime last wrote for a run folder.</summary>
    static string TrainingRunState(string folder)
    {
        try
        {
            string path = Path.Combine(folder, "status.json");
            if (!File.Exists(path)) return "Not started";
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var json = JsonDocument.Parse(file);
            return json.RootElement.TryGetProperty("state", out var state) ? state.GetString() ?? "Unknown" : "Unknown";
        }
        catch { return "Unknown"; }
    }

    FrameworkElement BuildGymHome()
    {
        // Hero: Spring, the tagline and one suggested next step derived from what is saved.
        var spring = Mascot("you-got-this", 168);
        var tagline = Heading("Practice makes possibility.", 38);
        tagline.LineHeight = 44;
        var bio = Muted("A place to practice with local AI. Shape a dataset, train an adapter, compare held-out results, and keep the evidence close.", 15);
        bio.MaxWidth = 640;
        bio.HorizontalAlignment = HorizontalAlignment.Left;
        var nextCaption = Label("", 14);
        string nextRoute = "Datasets";
        var nextButton = Button("Import a dataset", (_, _) => Navigate(nextRoute), true);
        var nextRow = new StackPanel { Spacing = 10, Margin = new Thickness(0, 6, 0, 0), Children = { nextCaption, nextButton } };
        nextButton.HorizontalAlignment = HorizontalAlignment.Left;
        // The suggestion is chosen from what is on disk, so it stays hidden until the first read finishes.
        nextRow.Opacity = 0;
        var heroText = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { Eyebrow("Local AI practice"), tagline, bio, nextRow } };
        var hero = new Grid { ColumnSpacing = 28, Padding = new Thickness(28, 26, 28, 40) };
        hero.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        hero.Children.Add(spring);
        Grid.SetColumn(heroText, 1);
        hero.Children.Add(heroText);
        // The hero sits on a settling spring that drifts with the scroll (see MainWindow.HomeArt.cs).
        var homeScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var heroCard = HomeBanner(homeScroll, hero);
        hero.SizeChanged += (_, _) =>
        {
            // Keep the hero readable in a narrow window: smaller mascot and headline.
            bool narrow = hero.ActualWidth < 640;
            spring.Width = spring.Height = narrow ? 104 : 168;
            tagline.FontSize = narrow ? 28 : 38;
            tagline.LineHeight = narrow ? 34 : 44;
        };

        // The loop: three cards that each show a real count and open the matching page.
        (Button Card, Action<int?, string, string> Set) LoopCard(int step, string title, string description, string glyph, string route, string routeLabel)
        {
            var figure = Heading("—", 34);
            var unit = Muted("", 13);
            unit.VerticalAlignment = VerticalAlignment.Bottom;
            unit.Margin = new Thickness(0, 0, 0, 7);
            var hint = Muted("", 12);
            // A plain accent icon per step; Spring appears once on this page, in the hero.
            var face = Xaml<FontIcon>($"<FontIcon {XamlNs} Foreground='{{ThemeResource GymAccentBrush}}' FontSize='26'/>");
            face.Glyph = glyph;
            face.HorizontalAlignment = HorizontalAlignment.Right;
            face.VerticalAlignment = VerticalAlignment.Top;
            face.Margin = new Thickness(0, 2, 0, 0);
            var top = new Grid();
            top.Children.Add(new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 40, 0), Children = { Eyebrow("Step " + step), Heading(title, 19) } });
            top.Children.Add(face);
            var open = Label(routeLabel + "  →", 13, "GymAccentBrush");
            open.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            // Until the profile folder has been read the count is a skeleton, never a guessed "0".
            var figureRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { figure, unit }, Visibility = Visibility.Collapsed };
            var figureSkeleton = Fluent.Skeleton(.4, .7);
            figureSkeleton.VerticalAlignment = VerticalAlignment.Center;
            var figureHost = new Grid { MinHeight = 46, Children = { figureRow, figureSkeleton } };
            var body = new StackPanel
            {
                Spacing = 6,
                Children = { top, figureHost, hint, Muted(description, 13), open }
            };
            var card = CardButton(body, $"{title}. {routeLabel}", () => Navigate(route));
            void Set(int? count, string noun, string detail)
            {
                if (figureRow.Visibility != Visibility.Visible) { figureHost.Children.Remove(figureSkeleton); figureRow.Visibility = Visibility.Visible; Fluent.Appear(figureRow, 4); }
                figure.Text = count?.ToString("N0") ?? "—";
                unit.Text = count == null ? "could not be read" : noun;
                hint.Text = detail;
                hint.Visibility = detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                AutomationProperties.SetName(card, $"{title}: {figure.Text} {unit.Text}. {routeLabel}");
            }
            return (card, Set);
        }
        var datasets = LoopCard(1, "Shape a dataset", "Import JSONL conversations, review them, and export an entity-separated bundle.", "", "Datasets", "Open Datasets");
        var train = LoopCard(2, "Train an adapter", "Prepare, run, stop and resume local LoRA training with retained checkpoints.", "", "Training", "Open Training lab");
        var compare = LoopCard(3, "Compare results", "Send the same held-out prompts to two saved connections and keep both answers.", "", "Compare", "Open Compare models");
        // Cards rise in one after another, glide when the grid reflows, and arrow keys move between them.
        var loop = Fluent.ArrowKeys(Fluent.Stagger(FlowGrid(250, datasets.Card, train.Card, compare.Card)));

        // Secondary destinations.
        (Button Card, TextBlock Detail) SideCard(string glyph, string title, string route, string name)
        {
            var detail = Muted("", 13);
            // A skeleton line stands in for the detail until the first read, then gives way to the text.
            var detailSkeleton = Fluent.Skeleton(.9, .55);
            detail.Visibility = Visibility.Collapsed;
            StackPanel? textPanel = null;
            detail.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
            {
                if (detail.Visibility == Visibility.Visible) return;
                textPanel?.Children.Remove(detailSkeleton);
                detail.Visibility = Visibility.Visible;
                Fluent.Appear(detail, 4);
            });
            var icon = Xaml<FontIcon>($"<FontIcon {XamlNs} Foreground='{{ThemeResource GymAccentBrush}}' FontSize='22' VerticalAlignment='Center'/>");
            icon.Glyph = glyph;
            var row = new Grid { ColumnSpacing = 14 };
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var text = new StackPanel { Spacing = 4, Children = { Heading(title, 16), detail, detailSkeleton } };
            textPanel = text;
            row.Children.Add(icon);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            return (CardButton(row, name, () => Navigate(route), 14), detail);
        }
        var lab = SideCard("", "Agent lab", "Agent lab", "Open Agent lab");
        var models = SideCard("", "Models & connections", "Models and connections", "Open Models and connections");
        var side = Fluent.ArrowKeys(Fluent.Stagger(FlowGrid(320, lab.Card, models.Card)));

        var loopHeader = new StackPanel { Spacing = 2, Margin = new Thickness(2, 8, 2, 0), Children = { Heading("The practice loop", 20), Muted("Counts are read from your AI Gym profile folder each time you open Home.", 13) } };
        var page = new StackPanel { Spacing = 16, MaxWidth = 1280, Margin = new Thickness(0, 4, 4, 16), Children = { heroCard, loopHeader, loop, side } };

        // The profile folder is read off the UI thread; until the first read lands the figures are skeletons, and
        // later visits keep the previous figures until the new ones arrive. Only the newest read is applied.
        int reads = 0;
        async void Refresh()
        {
            int read = ++reads;
            GymInventory have;
            try { have = await Task.Run(ReadGymInventory); }
            catch { have = new(null, null, null, null, null, null, null); }
            if (read != reads) return;
            if (nextRow.Opacity == 0) { nextRow.Opacity = 1; Fluent.Appear(nextRow, 6); }
            datasets.Set(have.Datasets, have.Datasets == 1 ? "dataset saved" : "datasets saved", string.Join(" · ", new[]
            {
                have.Examples is > 0 ? $"{have.Examples:N0} examples in total" : "",
                have.SkippedDatasets > 0 ? $"{have.SkippedDatasets:N0} unreadable file{(have.SkippedDatasets == 1 ? "" : "s")} skipped, see Datasets" : ""
            }.Where(x => x.Length > 0)));
            train.Set(have.TrainingRuns, have.TrainingRuns == 1 ? "training run retained" : "training runs retained", have.TrainingRuns > 0 && have.CompletedRuns != null ? $"{have.CompletedRuns:N0} completed" : "");
            compare.Set(have.Connections, have.Connections == 1 ? "saved model connection" : "saved model connections", have.Comparisons is > 0 ? $"{have.Comparisons:N0} comparison{(have.Comparisons == 1 ? "" : "s")} retained" : "");
            lab.Detail.Text = have.Experiments switch { null => "Saved experiments could not be read.", 0 => "Plan a bounded experiment and review every step. No experiments saved yet.", 1 => "1 saved experiment. Every tool step waits for your review.", _ => $"{have.Experiments:N0} saved experiments. Every tool step waits for your review." };
            models.Detail.Text = have.Connections switch { null => "Saved connections could not be read.", 0 => "Find local model files and save a connection. A saved connection is a configuration, not a running model.", _ => "Scan local model files, start a model, and manage saved connections." };

            // One suggestion, chosen only from what exists on disk.
            (nextCaption.Text, nextButton.Content, nextRoute) = have switch
            {
                { Datasets: null or 0 } => ("Start with data: import a JSONL file of conversations.", "Import a dataset", "Datasets"),
                { TrainingRuns: 0 } => ("You have data. Export a reviewed bundle, then prepare a training run.", "Open Training lab", "Training"),
                { Connections: null or < 2 } => ("Comparing needs two saved model connections.", "Open Models & connections", "Models and connections"),
                _ => ("Datasets, runs and connections are in place. Compare held-out responses when you are ready.", "Open Compare models", "Compare")
            };
        }
        refreshGymHome = Refresh;
        Refresh();
        homeScroll.Content = page;
        return homeScroll;
    }
}

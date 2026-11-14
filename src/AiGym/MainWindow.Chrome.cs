using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AiGym;

/// <summary>
/// Window chrome: content drawn into the title bar (WinUI TitleBar with the Spring lockup, a search over pages,
/// models and datasets, and the running model), Mica showing through the sidebar and caption area, page
/// transitions and window placement.
/// </summary>
public sealed partial class MainWindow
{
    TitleBar? titleBar;
    readonly AutoSuggestBox titleSearch = new() { PlaceholderText = "Search pages, models and datasets", QueryIcon = new SymbolIcon(Symbol.Find), MinWidth = 260, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
    Dictionary<string, Action> titleCommands = [];
    readonly ContentControl titleState = new() { IsTabStop = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 12, 0) };
    string titleStateKey = "";
    string? previousRoute;
    bool preferencesReadable = true;
    /// <summary>Searchable models and saved connections; set by the Models page once it has scanned.</summary>
    Func<IEnumerable<(string Label, Action Open)>>? modelSearchItems;
    /// <summary>Searchable saved datasets; set by the Datasets page.</summary>
    Func<IEnumerable<(string Label, Action Open)>>? datasetSearchItems;

    /// <summary>
    /// Extends the window into the caption area and puts a TitleBar there: pane toggle, the Spring lockup, the
    /// search in the middle and the running model on the right. The caption buttons stay the system's own.
    /// </summary>
    FrameworkElement BuildTitleBar(FrameworkElement brand)
    {
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        titleBar = new TitleBar
        {
            IsBackButtonVisible = false,
            IsPaneToggleButtonVisible = true,
            LeftHeader = brand,
            Content = titleSearch,
            RightHeader = titleState,
            Height = 48
        };
        // The pane toggle moves up into the title bar, as in Windows Settings; the NavigationView's own one goes.
        nav.IsPaneToggleButtonVisible = false;
        titleBar.PaneToggleRequested += (_, _) => nav.IsPaneOpen = !nav.IsPaneOpen;
        SetTitleBar(titleBar);

        AutomationProperties.SetName(titleSearch, "Search pages, models and datasets");
        ToolTipService.SetToolTip(titleSearch, "Search pages, models and datasets (Ctrl+K)");
        // Open the full list when the text field itself takes focus; focus moving onto a suggestion must not reopen it.
        titleSearch.GotFocus += (_, e) => { if (e.OriginalSource is not TextBox || titleSearchClosing) return; titleCommands = SearchCommands(); FilterTitleSearch(); DispatcherQueue.TryEnqueue(() => titleSearch.IsSuggestionListOpen = true); };
        titleSearch.TextChanged += (_, e) => { if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput) FilterTitleSearch(); };
        titleSearch.QuerySubmitted += (_, e) =>
        {
            string? key = e.ChosenSuggestion as string ?? titleCommands.Keys.FirstOrDefault(k => k.Contains(titleSearch.Text, StringComparison.OrdinalIgnoreCase));
            if (key == null || !titleCommands.TryGetValue(key, out var open)) return;
            CloseTitleSearch();
            open();
        };

        // The running model is live state, so it is re-read on a short timer instead of hooking every page that starts or stops one.
        var watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        watch.Tick += (_, _) => UpdateTitleState();
        root.Loaded += (_, _) => { UpdateTitleState(); watch.Start(); };
        Closed += (_, _) => watch.Stop();
        return titleBar;
    }

    /// <summary>Every page and view, then the scanned models and saved connections, then the saved datasets.</summary>
    Dictionary<string, Action> SearchCommands()
    {
        var commands = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase);
        void Add(string label, Action open) { var key = label; for (int n = 2; commands.ContainsKey(key); n++) key = $"{label} ({n})"; commands[key] = open; }
        Add("Home", () => Navigate("Home"));
        Add("Datasets", () => Navigate("Datasets"));
        Add("Training lab", () => Navigate("Training"));
        Add("Compare models", () => Navigate("Compare"));
        Add("Agent lab", () => Navigate("Agent lab"));
        foreach (var (label, index) in new[] { ("Model library", 0), ("Running models", 1), ("Runtime setup", 2), ("Find models on Hugging Face", 3), ("Model downloads", 4) })
            Add("Models · " + label, () => { Navigate("Models"); selectModelView?.Invoke(index); });
        Add("Connections", () => Navigate("AI connections"));
        try { foreach (var (label, open) in modelSearchItems?.Invoke() ?? []) Add(label, open); } catch { }
        try { foreach (var (label, open) in datasetSearchItems?.Invoke() ?? []) Add(label, open); } catch { }
        return commands;
    }

    bool titleSearchClosing;

    /// <summary>
    /// Clears and closes the title bar search and hands focus back to the current page's navigation item, so the
    /// list cannot linger and a command that opens a dialog does not fight the popup.
    /// </summary>
    void CloseTitleSearch()
    {
        titleSearchClosing = true;
        titleSearch.Text = ""; titleSearch.ItemsSource = null; titleSearch.IsSuggestionListOpen = false;
        (nav.SelectedItem as Control)?.Focus(FocusState.Programmatic);
        DispatcherQueue.TryEnqueue(() => { titleSearch.IsSuggestionListOpen = false; titleSearchClosing = false; });
    }

    void FilterTitleSearch()
    {
        var text = titleSearch.Text.Trim();
        titleSearch.ItemsSource = titleCommands.Keys.Where(k => text.Length == 0 || k.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(40).ToList();
    }

    void FocusTitleSearch() => titleSearch.Focus(FocusState.Keyboard);

    /// <summary>The models AI Gym is serving ride in the title bar, so they stay in view on every page.</summary>
    void UpdateTitleState()
    {
        var live = modelServing.Running.Where(x => x.Owner.IsRunning).ToArray();
        string key = string.Join("|", live.Select(x => x.Id));
        if (key == titleStateKey) return;
        titleStateKey = key;
        if (live.Length == 0) { titleState.Content = null; return; }
        var pill = Pill(live.Length == 1 ? "Running · " + ModelLibraryCard.FriendlyName(live[0].Model.Name) : $"{live.Length} models running", GymTone.Accent);
        if (pill.Child is TextBlock text) { text.TextTrimming = TextTrimming.CharacterEllipsis; text.MaxWidth = 280; }
        ToolTipService.SetToolTip(pill, string.Join("\n", live.Select(x => ModelLibraryCard.FriendlyName(x.Model.Name) + " · " + x.Profile.Endpoint)));
        AutomationProperties.SetName(pill, live.Length == 1 ? "Running in AI Gym: " + ModelLibraryCard.FriendlyName(live[0].Model.Name) : $"{live.Length} models running in AI Gym");
        titleState.Content = pill;
        Fluent.Appear(pill, 4);
    }

    /// <summary>
    /// Lets Mica show through the sidebar and title bar while the page itself keeps the brand page colour, with the
    /// rounded top-left corner of a Windows 11 content area. High contrast keeps solid system colours.
    /// </summary>
    void ApplyWindowMaterial(bool dark)
    {
        var chrome = AppWindow.TitleBar;
        if (Fluent.HighContrast)
        {
            root.Background = new SolidColorBrush(brandSystemColors.GetColorValue(UIColorType.Background));
            chrome.ButtonBackgroundColor = chrome.ButtonInactiveBackgroundColor = null;
            chrome.ButtonForegroundColor = chrome.ButtonInactiveForegroundColor = chrome.ButtonHoverBackgroundColor = chrome.ButtonHoverForegroundColor = null;
            chrome.ButtonPressedBackgroundColor = chrome.ButtonPressedForegroundColor = null;
            return;
        }
        root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var ink = dark ? Color.FromArgb(255, 244, 248, 246) : Color.FromArgb(255, 16, 44, 43);
        chrome.ButtonBackgroundColor = chrome.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        chrome.ButtonForegroundColor = chrome.ButtonHoverForegroundColor = chrome.ButtonPressedForegroundColor = ink;
        chrome.ButtonInactiveForegroundColor = dark ? Color.FromArgb(255, 183, 210, 203) : Color.FromArgb(255, 64, 95, 93);
        chrome.ButtonHoverBackgroundColor = dark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);
        chrome.ButtonPressedBackgroundColor = dark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(12, 0, 0, 0);
    }

    /// <summary>The page area keeps the brand page colour and gets the Windows 11 content edge where it meets the Mica sidebar.</summary>
    void ApplyShellSurface()
    {
        foreach (var (theme, page, edge) in new[] { ("Light", Color.FromArgb(255, 244, 248, 246), Color.FromArgb(64, 102, 127, 121)), ("Default", Color.FromArgb(255, 11, 32, 32), Color.FromArgb(46, 183, 210, 203)) })
        {
            var dictionary = (ResourceDictionary)nav.Resources.ThemeDictionaries[theme];
            foreach (var key in new[] { "NavigationViewDefaultPaneBackground", "NavigationViewExpandedPaneBackground", "NavigationViewTopPaneBackground" }) dictionary[key] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            dictionary["NavigationViewContentBackground"] = new SolidColorBrush(page);
            dictionary["NavigationViewContentGridBorderBrush"] = new SolidColorBrush(edge);
        }
        nav.Resources["NavigationViewContentGridCornerRadius"] = new CornerRadius(8, 0, 0, 0);
        nav.Resources["NavigationViewContentGridBorderThickness"] = new Thickness(1, 1, 0, 0);
    }

    /// <summary>
    /// Which way a page enters: from a Home card it drills in; moving down the navigation it rises from below, moving
    /// up it settles from above, like the stacked pages of Windows Settings.
    /// </summary>
    Fluent.PageMotion MotionFor(string from, string to)
    {
        if (from == "Home" && to != "Home") return Fluent.PageMotion.DrillIn;
        var order = nav.MenuItems.Concat(nav.FooterMenuItems).OfType<NavigationViewItem>().Select(i => i.Tag as string).ToList();
        int a = order.IndexOf(from), b = order.IndexOf(to);
        return a >= 0 && b >= 0 && b < a ? Fluent.PageMotion.EntranceFromAbove : Fluent.PageMotion.Entrance;
    }

    // ── Window placement ────────────────────────────────────────────────────────────────────────────

    /// <summary>Reopens the window where it was, at its size, maximized if it was; centred on first run.</summary>
    void RestorePlacement()
    {
        var saved = preferences.Window;
        try
        {
            if (saved is { Width: > 400, Height: > 300 })
            {
                var rect = new Windows.Graphics.RectInt32(saved.X, saved.Y, saved.Width, saved.Height);
                // Only restore onto a display that still exists; otherwise fall back to the primary one.
                var area = DisplayArea.GetFromRect(rect, DisplayAreaFallback.None);
                if (area != null) { AppWindow.MoveAndResize(rect); if (saved.Maximized && AppWindow.Presenter is OverlappedPresenter p) p.Maximize(); return; }
            }
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            int width = Math.Min(1640, work.Width - 80), height = Math.Min(1060, work.Height - 60);
            AppWindow.MoveAndResize(new(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
        }
        catch { /* placement is a convenience */ }
    }

    void TrackPlacement()
    {
        AppWindow.Changed += (sender, args) =>
        {
            if (!(args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange)) return;
            bool maximized = sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            bool minimized = sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
            if (minimized) return;
            // While maximized keep the restored bounds, so un-maximizing next time lands in the old place.
            var previous = preferences.Window;
            preferences = preferences with
            {
                Window = maximized && previous != null
                    ? previous with { Maximized = true }
                    : new WindowPlacement(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height, maximized)
            };
        };
    }

    /// <summary>Saves the placement with the appearance choice; an unreadable appearance file is left untouched.</summary>
    void SavePlacement()
    {
        if (!preferencesReadable || preferences.Window == null) return;
        try { Store.Write("appearance.json", preferences); } catch { /* placement is a convenience */ }
    }
}

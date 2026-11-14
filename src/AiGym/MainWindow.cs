using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
namespace AiGym;

public sealed partial class MainWindow : Window
{
    readonly Grid root = new() { RequestedTheme = ElementTheme.Default };
    readonly NavigationView nav = new() { IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, IsSettingsVisible = false, PaneDisplayMode = NavigationViewPaneDisplayMode.Auto, CompactModeThresholdWidth = 0, ExpandedModeThresholdWidth = 1200, OpenPaneLength = 272, IsTitleBarAutoPaddingEnabled = false };
    readonly TextBlock status = Text("Ready · local AI workspace", 12);
    readonly InfoBar feedback = new() { IsOpen = false, IsClosable = true };
    readonly Dictionary<string, FrameworkElement> pages = [];
    bool dialogOpen; AppPreferences preferences = new();
    static TextBlock Text(string text, double size = 14)
    {
        var ink=new SolidColorBrush();var label=new TextBlock{Text=text,FontSize=size<=13?size+1:size,TextWrapping=TextWrapping.Wrap,Foreground=ink};
        void Theme(){ink.Color=new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast?new Windows.UI.ViewManagement.UISettings().UIElementColor(Windows.UI.ViewManagement.UIElementType.WindowText):label.ActualTheme==ElementTheme.Dark?Windows.UI.Color.FromArgb(255,240,239,235):Windows.UI.Color.FromArgb(255, 16, 44, 43);}
        label.Loaded+=(_,_)=>Theme();label.ActualThemeChanged+=(_,_)=>Theme();Theme();
        // A contrast theme can be switched on without changing ActualTheme in an unpackaged app (see BrandAccent).
        void ColorsChanged(Windows.UI.ViewManagement.UISettings _,object __)=>label.DispatcherQueue.TryEnqueue(Theme);
        label.Loaded+=(_,_)=>{brandSystemColors.ColorValuesChanged-=ColorsChanged;brandSystemColors.ColorValuesChanged+=ColorsChanged;};
        label.Unloaded+=(_,_)=>brandSystemColors.ColorValuesChanged-=ColorsChanged;
        return label;
    }
    static Button Button(string text, RoutedEventHandler handler, bool accent = false) { var b = new Button { Content = text, Padding = new Thickness(13, 7, 13, 7), VerticalAlignment = VerticalAlignment.Center }; b.Click += handler; if (accent) { b.Style = (Style)Application.Current.Resources["AccentButtonStyle"]; BrandAccent(b); } return b; }
    static StackPanel Row(params UIElement[] items) { var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; foreach (var i in items) p.Children.Add(i); return p; }
    static StackPanel Stack(params UIElement[] items) { var p = new StackPanel { Spacing = 10 }; foreach (var i in items) p.Children.Add(i); return p; }
    static ScrollViewer ScrollTools(FrameworkElement content) => new() { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    static Grid Vertical(FrameworkElement top, FrameworkElement body) { var g = new Grid { RowSpacing = 10 }; g.RowDefinitions.Add(new() { Height = GridLength.Auto }); g.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); g.Children.Add(top); Grid.SetRow(body, 1); g.Children.Add(body); return g; }
    public MainWindow()
    {
        Title = "AI Gym";
        SystemBackdrop = new MicaBackdrop(); Content = root;
        try { preferences = Store.Read<AppPreferences>("appearance.json") ?? new(); }
        catch (Exception error) { preferencesReadable = false; status.Text = "Appearance could not be read: " + error.Message; }
        // TryParse also accepts numbers such as "3", so only named themes are applied.
        root.RequestedTheme = Enum.TryParse<ElementTheme>(preferences.Theme, out var theme) && Enum.IsDefined(theme) ? theme : ElementTheme.Default;
        // Every animation checks the Windows animation setting and this app's own reduced-motion preference.
        Fluent.ReducedMotion = () => preferences.ReducedMotion;
        RestorePlacement(); TrackPlacement();
        ApplyTheme(); root.ActualThemeChanged += (_, _) => ApplyTheme();
        // Turning a contrast theme on or off may not change ActualTheme here, so also follow the system colours.
        brandSystemColors.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyTheme);
        Fluent.FollowContrast(root);
        BrandControl(nav);
        // Mica shows through the sidebar and title bar; the page keeps the brand page colour (MainWindow.Chrome.cs).
        ApplyShellSurface();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var brand = BrandHeader();
        root.Children.Add(BuildTitleBar(brand));
        Grid.SetRow(nav, 1); root.Children.Add(nav);
        BuildNavigation();
        nav.PaneFooter = BuildThemeSwitch();
        nav.ItemInvoked += (_, e) =>
        {
            if (e.InvokedItemContainer is not NavigationViewItem { Tag: string route } item) return;
            DispatcherQueue.TryEnqueue(() => Navigate(route));
        };
        // UI Automation and keyboard selection change the selected item without invoking it.
        nav.SelectionChanged += (_, e) => { if (e.SelectedItem is NavigationViewItem { Tag: string route } && route != currentRoute && pages.ContainsKey(route)) ShowPage(route); };
        pages["Training"] = BuildGymTraining();
        pages["Agent lab"] = BuildGymLab();
        pages["Datasets"] = BuildGymDatasets();
        pages["Compare"] = BuildGymComparison();
        var endpoints = BuildAiConnections();
        var library = BuildModelLibrary();
        var resources = ModeSwitcher(("Models", library), ("Connections", endpoints));
        selectResources = index => resources.SelectedIndex = index;
        pages["Models and connections"] = resources;
        // Built last so its counters can read what the other pages loaded.
        pages["Home"] = BuildGymHome();

        var content = new Grid { Padding = new Thickness(24, 14, 24, 12) };
        // Cards cast their hover shadow onto this empty layer behind the pages (a ThemeShadow receiver may not contain its caster).
        var shadowLayer = new Grid(); content.Children.Add(shadowLayer); Fluent.AddShadowReceiver(shadowLayer);
        foreach(var view in pages.Values) { view.Visibility = Visibility.Collapsed; content.Children.Add(view); }
        var shell = Vertical(content, Stack(feedback, BuildStatusStrip()));
        shell.RowSpacing = 0;
        shell.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        shell.RowDefinitions[1].Height = GridLength.Auto;
        nav.Content = shell;
        Navigate("Home");
        root.KeyboardAccelerators.Add(Shortcut(VirtualKey.Escape, VirtualKeyModifiers.None, () =>
        {
            // Esc in a text or choice field means "leave this field", not "stop the running work".
            if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root.XamlRoot) is TextBox or PasswordBox or NumberBox or ComboBox or AutoSuggestBox) return;
            gymOperation?.Cancel(); aiOperation?.Cancel();
        }));
        root.KeyboardAccelerators.Add(Shortcut(VirtualKey.K, VirtualKeyModifiers.Control, FocusTitleSearch));
        AppWindow.Closing += async (_, e) =>
        {
            if(gymOperation != null || aiOperation != null || modelScanBusy?.Invoke() == true)
            {
                gymOperation?.Cancel(); aiOperation?.Cancel(); cancelModelScan?.Invoke();
                e.Cancel = true; status.Text = "Stopping active work. Close again when it finishes."; return;
            }
            if(!downloadsClosed && modelDownloads != null)
            {
                e.Cancel = true; if(downloadsClosing) return; downloadsClosing = true;
                try { await modelDownloads.DisposeAsync(); }
                catch(Exception error) { await Notice("Downloads paused", error.Message); }
                finally { downloadsClosed = true; }
                Close(); return;
            }
            SavePlacement();
            modelServing.Dispose();
        };
    }
    /// <summary>The Auto / Light / Dark choice at the foot of the sidebar. It applies at once and is saved.</summary>
    ThemeSwitch BuildThemeSwitch()
    {
        var theme = new ThemeSwitch(root.RequestedTheme, segment => BrandControl(segment)) { Margin = new Thickness(12, 4, 12, 10) };
        theme.Changed += async chosen =>
        {
            try { Store.Write("appearance.json", preferences with { Theme = chosen.ToString() }); root.RequestedTheme = chosen; preferences = preferences with { Theme = chosen.ToString() }; }
            catch (Exception error)
            {
                // The window keeps its current theme, so the switch must show it too.
                theme.Display(root.RequestedTheme);
                await Notice("Appearance could not be saved", error.Message);
            }
        };
        return theme;
    }
    /// <summary>Slim strip under every page: what the app is doing now, and the build that is running.</summary>
    FrameworkElement BuildStatusStrip()
    {
        var dot = Xaml<Microsoft.UI.Xaml.Shapes.Ellipse>($"<Ellipse {XamlNs} Width='8' Height='8' Fill='{{ThemeResource GymAccentBrush}}' VerticalAlignment='Center'/>");
        var busy = BrandControl(new ProgressRing { Width = 14, Height = 14, IsActive = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center });
        status.FontSize = 12.5; status.Opacity = .86; status.TextWrapping = TextWrapping.NoWrap; status.TextTrimming = TextTrimming.CharacterEllipsis; status.VerticalAlignment = VerticalAlignment.Center;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var version = Muted("AI Gym " + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? ""), 12);
        version.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var lead = new Grid { Width = 14, Children = { dot, busy } };
        row.Children.Add(lead); Grid.SetColumn(status, 1); row.Children.Add(status); Grid.SetColumn(version, 2); row.Children.Add(version);
        // A lab operation swaps the idle dot for a progress ring; the text is still set by whoever owns the work.
        gymBusyListeners.Add(() =>
        {
            bool active = gymOperation != null;
            busy.IsActive = active; busy.Visibility = active ? Visibility.Visible : Visibility.Collapsed; dot.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
            status.Text = active ? "Working · press Esc to stop at the next safe point" : "Ready · local AI workspace";
        });
        var strip = Xaml<Border>($"<Border {XamlNs} BorderBrush='{{ThemeResource GymBorderBrush}}' BorderThickness='0,1,0,0' Padding='24,7,24,8'/>");
        strip.Child = row;
        return strip;
    }
    KeyboardAccelerator Shortcut(VirtualKey key, VirtualKeyModifiers modifiers, Action action) { var a = new KeyboardAccelerator { Key = key, Modifiers = modifiers }; a.Invoked += (_, e) => { e.Handled = true; action(); }; return a; }
    void ShowPage(string name)
    {
        if(!pages.ContainsKey(name)) return;
        currentRoute = name;
        if(name == "Models and connections") refreshModelLibrary?.Invoke();
        if(name == "Home") refreshGymHome?.Invoke();
        // Connections and datasets can change on other pages; keep the lab pickers current.
        if(name is "Compare" or "Agent lab" && gymOperation == null) refreshGymDatasets?.Invoke();
        bool changed = pages[name].Visibility != Visibility.Visible;
        string from = previousRoute ?? name; previousRoute = name;
        foreach(var view in pages) view.Value.Visibility = view.Key == name ? Visibility.Visible : Visibility.Collapsed;
        // A Home card hands off to its page (connected animation); otherwise the page enters the way navigation moved.
        if(changed && !Fluent.Connect(HomeCardAnimation, pages[name])) Fluent.PageIn(pages[name], MotionFor(from, name));
    }
    void Navigate(string name)
    {
        if(name is "Models" or "AI connections") { selectResources?.Invoke(name == "AI connections" ? 1 : 0); name = "Models and connections"; }
        if(!pages.ContainsKey(name)) return;
        ShowPage(name);
        nav.SelectedItem = nav.MenuItems.Concat(nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(x => Equals(x.Tag, name));
    }
}

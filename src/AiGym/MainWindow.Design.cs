using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace AiGym;

// AI Gym design system: small, reusable building blocks on top of the brand tokens
// declared as theme resources in App.xaml (Gym*Brush). Elements are created through
// XamlReader so {ThemeResource} keeps following light, dark and high-contrast changes.
public sealed partial class MainWindow
{
    enum GymTone { Neutral, Accent, Warn, Danger }

    const string XamlNs = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'";

    // Manrope is the brand display face; body text and controls stay on the system font.
    static readonly FontFamily DisplayFont = new("ms-appx:///Assets/Fonts/Manrope.ttf#Manrope");

    static T Xaml<T>(string markup) => (T)XamlReader.Load(markup);

    /// <summary>The Windows animation setting together with this app's reduced-motion preference.</summary>
    static bool AnimationsEnabled() => Fluent.MotionOn;

    /// <summary>A text block whose colour follows a brand theme resource.</summary>
    static TextBlock Label(string text, double size = 14, string brush = "GymInkBrush")
    {
        var label = Xaml<TextBlock>($"<TextBlock {XamlNs} Foreground='{{ThemeResource {brush}}}' TextWrapping='Wrap'/>");
        label.Text = text;
        label.FontSize = size;
        return label;
    }

    static TextBlock Muted(string text, double size = 13) => Label(text, size, "GymMutedBrush");

    /// <summary>Display heading in Manrope.</summary>
    static TextBlock Heading(string text, double size = 20)
    {
        var label = Label(text, size);
        Display(label);
        return label;
    }

    static TextBlock Display(TextBlock label)
    {
        label.FontFamily = DisplayFont;
        label.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        return label;
    }

    /// <summary>Small upper-case accent caption used above headings and step titles.</summary>
    static TextBlock Eyebrow(string text)
    {
        var label = Label(text.ToUpperInvariant(), 11, "GymAccentBrush");
        label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        label.CharacterSpacing = 90;
        return label;
    }

    /// <summary>A bordered brand surface. <paramref name="background"/> is a Gym*Brush key.</summary>
    static Border Surface(FrameworkElement? child, string background = "GymSurfaceBrush", double radius = 20, double padding = 20, bool border = true, string borderBrush = "GymBorderBrush")
    {
        var surface = Xaml<Border>($"<Border {XamlNs} Background='{{ThemeResource {background}}}' BorderBrush='{{ThemeResource {borderBrush}}}'/>");
        // In high contrast every fill resolves to the window colour, so an outline is the only edge a surface has.
        surface.BorderThickness = new Thickness(border || new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ? 1 : 0);
        surface.CornerRadius = new CornerRadius(radius);
        surface.Padding = new Thickness(padding);
        surface.Child = child;
        return surface;
    }

    /// <summary>Status pill. Colour is never the only signal: the text always states the state.</summary>
    static Border Pill(string text, GymTone tone = GymTone.Neutral)
    {
        var (fill, ink) = tone switch
        {
            GymTone.Accent => ("GymTintBrush", "GymAccentBrush"),
            GymTone.Warn => ("GymWarnTintBrush", "GymWarnBrush"),
            GymTone.Danger => ("GymDangerTintBrush", "GymDangerBrush"),
            _ => ("GymSurfaceAltBrush", "GymMutedBrush")
        };
        var label = Label(text, 12, ink);
        label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        label.TextWrapping = TextWrapping.NoWrap;
        // Neutral pills carry an outline so they stay visible on tinted panels.
        var pill = Surface(label, fill, 11, 0, border: tone == GymTone.Neutral);
        pill.Padding = new Thickness(10, 3, 10, 4);
        pill.VerticalAlignment = VerticalAlignment.Center;
        pill.HorizontalAlignment = HorizontalAlignment.Left;
        return pill;
    }

    /// <summary>Maps the engine's state words onto a pill tone without inventing new states.</summary>
    static GymTone StateTone(string state) => state switch
    {
        "Completed" or "Ready" => GymTone.Accent,
        "Failed" or "Error" => GymTone.Danger,
        "Cancelled" or "Interrupted" or "SetupRequired" or "AwaitingReview" or "Paused" => GymTone.Warn,
        "Training" or "Running" or "Starting" or "Planning" => GymTone.Accent,
        _ => GymTone.Neutral
    };

    /// <summary>A labelled figure. The setter replaces value and hint; "—" means not observed.</summary>
    static (Border View, Action<string, string> Set) StatTile(string label)
    {
        var value = Heading("—", 26);
        value.TextWrapping = TextWrapping.NoWrap;
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        var hint = Muted("", 12);
        hint.Visibility = Visibility.Collapsed;
        var caption = Muted(label, 12);
        var body = new StackPanel { Spacing = 2, Children = { caption, value, hint } };
        var tile = Surface(body, "GymSurfaceAltBrush", 12, 14, border: false);
        AutomationProperties.SetName(tile, label);
        void Set(string text, string detail)
        {
            value.Text = text;
            hint.Text = detail;
            hint.Visibility = detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            ToolTipService.SetToolTip(tile, detail.Length == 0 ? $"{label}: {text}" : $"{label}: {text} · {detail}");
            AutomationProperties.SetName(tile, $"{label}: {text}");
        }
        return (tile, Set);
    }

    /// <summary>Lays children out in equal columns and wraps to fewer columns when narrow.</summary>
    static Grid FlowGrid(double minColumnWidth, params FrameworkElement[] items)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        foreach (var item in items) grid.Children.Add(item);
        int current = 0;
        void Arrange()
        {
            int visible = items.Count(i => i.Visibility == Visibility.Visible);
            if (visible == 0) return;
            double width = double.IsNaN(grid.ActualWidth) || grid.ActualWidth == 0 ? minColumnWidth * visible : grid.ActualWidth;
            int columns = Math.Clamp((int)((width + grid.ColumnSpacing) / (minColumnWidth + grid.ColumnSpacing)), 1, visible);
            if (columns == current && grid.RowDefinitions.Count == (visible + columns - 1) / columns) return;
            current = columns;
            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();
            for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            for (int r = 0; r < (visible + columns - 1) / columns; r++) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            int index = 0;
            foreach (var item in items.Where(i => i.Visibility == Visibility.Visible))
            {
                Grid.SetColumn(item, index % columns);
                Grid.SetRow(item, index / columns);
                index++;
            }
        }
        grid.SizeChanged += (_, _) => Arrange();
        grid.Loaded += (_, _) => Arrange();
        Arrange();
        return grid;
    }

    /// <summary>Two panes side by side that stack once the host is narrower than <paramref name="threshold"/>.</summary>
    static Grid TwoPane(FrameworkElement first, FrameworkElement second, double firstWeight = 1, double secondWeight = 1, double threshold = 900)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 16 };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(firstWeight, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(secondWeight, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        first.VerticalAlignment = second.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(first);
        grid.Children.Add(second);
        bool? stacked = null;
        void Arrange()
        {
            bool narrow = grid.ActualWidth > 0 && grid.ActualWidth < threshold;
            if (narrow == stacked) return;
            stacked = narrow;
            grid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(secondWeight, GridUnitType.Star);
            Grid.SetColumnSpan(first, 1);
            Grid.SetColumn(second, narrow ? 0 : 1);
            Grid.SetRow(second, narrow ? 1 : 0);
        }
        grid.SizeChanged += (_, _) => Arrange();
        Grid.SetColumn(second, 1);
        return grid;
    }

    /// <summary>A numbered step card: eyebrow, Manrope title, one line of guidance, then content.</summary>
    static Border StepCard(string step, string title, string description, params UIElement[] body)
    {
        var header = new StackPanel { Spacing = 2, Children = { Eyebrow(step), Heading(title, 20) } };
        var panel = new StackPanel { Spacing = 14, Children = { header } };
        if (description.Length > 0)
        {
            header.Children.Add(Muted(description, 13));
        }
        foreach (var item in body) panel.Children.Add(item);
        return Surface(panel);
    }

    /// <summary>Icon-only button with a tooltip and an accessible name.</summary>
    static Button IconButton(string glyph, string name, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 15 },
            Padding = new Thickness(9, 7, 9, 7),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        button.Click += handler;
        ToolTipService.SetToolTip(button, name);
        AutomationProperties.SetName(button, name);
        return button;
    }

    /// <summary>Read-only monospaced block for exact values (arguments, hashes, raw evidence).</summary>
    static Border MonoBlock(string text, double maxHeight = 220)
    {
        var label = Label(text, 12.5);
        label.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        label.IsTextSelectionEnabled = true;
        var scroll = new ScrollViewer
        {
            Content = label,
            MaxHeight = maxHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        return Surface(scroll, "GymSurfaceAltBrush", 8, 12, border: false);
    }

    static void SetMono(Border block, string text)
    {
        if (block.Child is ScrollViewer { Content: TextBlock label }) label.Text = text;
    }

    /// <summary>Shows a path or fingerprint with copy and (for folders) open actions.</summary>
    static FrameworkElement ValueRow(string label, string value, bool folder = false)
    {
        var caption = Muted(label, 12);
        var text = Label(value, 12.5);
        text.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        text.IsTextSelectionEnabled = true;
        text.TextWrapping = TextWrapping.NoWrap;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(text, value);
        var copy = IconButton("", "Copy " + label.ToLowerInvariant(), (_, _) =>
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(value);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        });
        copy.VerticalAlignment = VerticalAlignment.Center;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { copy } };
        if (folder)
        {
            var open = IconButton("", "Open folder", (_, _) =>
            {
                // Only ever opens an existing directory in File Explorer.
                try { if (Directory.Exists(value)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + value + "\"") { UseShellExecute = true }); }
                catch { }
            });
            open.VerticalAlignment = VerticalAlignment.Center;
            actions.Children.Add(open);
        }
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { caption, text } });
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);
        return Surface(row, "GymSurfaceAltBrush", 8, 10, border: false);
    }

    /// <summary>One message of a conversation, drawn as a chat bubble. Long bodies are truncated with an explicit expander.</summary>
    static FrameworkElement Bubble(string role, string content)
    {
        const int limit = 1200;
        bool user = role == "user", system = role == "system";
        var who = Muted(role, 11);
        who.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var text = Label(content.Length > limit ? content[..limit] + "…" : content, 13.5);
        text.IsTextSelectionEnabled = true;
        var body = new StackPanel { Spacing = 3, Children = { who, text } };
        if (content.Length > limit)
        {
            var more = new HyperlinkButton { Content = $"Show all {content.Length:N0} characters", Padding = new Thickness(0) };
            more.Click += (_, _) => { text.Text = content; more.Visibility = Visibility.Collapsed; };
            body.Children.Add(more);
        }
        var bubble = Surface(body, user ? "GymTintBrush" : "GymSurfaceAltBrush", 14, 12, border: system);
        bubble.MaxWidth = 720;
        bubble.HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        // Leave a gutter on the opposite side so the speaker is readable without colour.
        bubble.Margin = user ? new Thickness(48, 0, 0, 0) : new Thickness(0, 0, 48, 0);
        AutomationProperties.SetName(bubble, role + " message");
        return bubble;
    }

    /// <summary>Connected-animation key: the Home card the user chose grows into the page it opens.</summary>
    const string HomeCardAnimation = "gym-card";

    /// <summary>
    /// A card that behaves like a button (hover, pressed, focus, keyboard, UI Automation invoke). It rises toward
    /// the pointer and hands off to the page it opens.
    /// </summary>
    static Button CardButton(FrameworkElement content, string name, Action invoke, double radius = 20)
    {
        var button = Xaml<Button>(
            $"<Button {XamlNs} HorizontalAlignment='Stretch' VerticalAlignment='Stretch' HorizontalContentAlignment='Stretch' VerticalContentAlignment='Stretch'>" +
            "<Button.Resources><ResourceDictionary><ResourceDictionary.ThemeDictionaries>" +
            CardButtonTheme("Light") + CardButtonTheme("Default") +
            "<ResourceDictionary x:Key='HighContrast' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'/>" +
            "</ResourceDictionary.ThemeDictionaries></ResourceDictionary></Button.Resources></Button>");
        button.Content = content;
        button.Padding = new Thickness(18);
        button.CornerRadius = new CornerRadius(radius);
        button.Click += (_, _) => { Fluent.Prepare(HomeCardAnimation, button); invoke(); };
        AutomationProperties.SetName(button, name);
        return Fluent.Lift(button);
    }

    // Explicit colours per theme: an alias to a Gym*Brush would be resolved once, against the
    // app theme at parse time, and would not follow a theme the user picks in the app.
    static string CardButtonTheme(string theme)
    {
        bool light = theme == "Light";
        string surface = light ? "#FFFFFF" : "#153332", hover = light ? "#F0F7F4" : "#1B3F3D", pressed = light ? "#E2EEE9" : "#113030";
        string border = light ? "#4D667F79" : "#33B7D2CB", accent = light ? "#006B58" : "#76E4C0", ink = light ? "#102C2B" : "#F4F8F6";
        string Brush(string key, string color) => $"<SolidColorBrush x:Key='{key}' Color='{color}'/>";
        return $"<ResourceDictionary x:Key='{theme}' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
            Brush("ButtonBackground", surface) + Brush("ButtonBackgroundPointerOver", hover) + Brush("ButtonBackgroundPressed", pressed) +
            Brush("ButtonBorderBrush", border) + Brush("ButtonBorderBrushPointerOver", accent) + Brush("ButtonBorderBrushPressed", accent) +
            Brush("ButtonForeground", ink) + Brush("ButtonForegroundPointerOver", ink) + Brush("ButtonForegroundPressed", ink) +
            "</ResourceDictionary>";
    }

    /// <summary>Proportional two-part bar for the train / held-out split, with a text legend.</summary>
    static FrameworkElement SplitBar(int train, int heldOut)
    {
        var bar = new Grid { Height = 14, ColumnSpacing = 3 };
        bar.ColumnDefinitions.Add(new() { Width = new GridLength(Math.Max(train, 0.0001), GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new() { Width = new GridLength(Math.Max(heldOut, 0.0001), GridUnitType.Star) });
        var trainPart = Surface(null, "GymAccentBrush", 7, 0, border: false);
        var heldPart = Surface(null, "GymHeldOutBrush", 7, 0, border: false);
        Grid.SetColumn(heldPart, 1);
        bar.Children.Add(trainPart);
        bar.Children.Add(heldPart);
        int total = Math.Max(1, train + heldOut);
        FrameworkElement Legend(string brush, string text)
        {
            var dot = Xaml<Ellipse>($"<Ellipse {XamlNs} Width='10' Height='10' Fill='{{ThemeResource {brush}}}' VerticalAlignment='Center'/>");
            return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { dot, Label(text, 13) } };
        }
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
        legend.Children.Add(Legend("GymAccentBrush", $"Training · {train:N0} examples ({train * 100.0 / total:0.#}%)"));
        legend.Children.Add(Legend("GymHeldOutBrush", $"Held out · {heldOut:N0} examples ({heldOut * 100.0 / total:0.#}%)"));
        AutomationProperties.SetName(bar, $"Split: {train} training examples, {heldOut} held-out examples");
        return new StackPanel { Spacing = 8, Children = { bar, new ScrollViewer { Content = legend, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled } } };
    }

    /// <summary>Page-level message strip. Returns the view and a setter; an empty message closes it.</summary>
    static (InfoBar View, Action<string, InfoBarSeverity> Show) GymNotice()
    {
        var bar = new InfoBar { IsOpen = false, IsClosable = true };
        void Show(string message, InfoBarSeverity severity)
        {
            bar.Message = message;
            bar.Severity = severity;
            bar.IsOpen = message.Length > 0;
            // Messages can arrive while the page is scrolled down; bring the strip back into view.
            if (message.Length > 0) bar.StartBringIntoView();
        }
        return (bar, Show);
    }

    static string ShortHash(string hash) => hash.Length > 16 ? hash[..8] + "…" + hash[^6..] : hash;

    static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s" : $"{span.TotalSeconds:0.#}s";
    }
}

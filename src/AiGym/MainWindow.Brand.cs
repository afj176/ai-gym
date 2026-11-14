using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AiGym;

public sealed partial class MainWindow
{
    static readonly UISettings brandSystemColors = new();
    static void BrandAccent(Button button)
    {
        void Update()
        {
            string[] keys = ["AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed", "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed"];
            if (new AccessibilitySettings().HighContrast)
            {
                foreach (var key in keys) button.Resources.Remove(key);
                button.ClearValue(Control.BackgroundProperty); button.ClearValue(Control.ForegroundProperty); return;
            }
            bool dark = button.ActualTheme == ElementTheme.Dark;
            var fill = new SolidColorBrush(dark ? Color.FromArgb(255, 118, 228, 192) : Color.FromArgb(255, 0, 107, 88));
            var hover = new SolidColorBrush(dark ? Color.FromArgb(255, 146, 236, 207) : Color.FromArgb(255, 0, 91, 75));
            var pressed = new SolidColorBrush(dark ? Color.FromArgb(255, 86, 196, 160) : Color.FromArgb(255, 0, 76, 62));
            var foreground = new SolidColorBrush(dark ? Color.FromArgb(255, 11, 32, 32) : Color.FromArgb(255, 255, 255, 255));
            foreach (var key in keys) button.Resources[key] = key.Contains("Foreground") ? foreground : key.EndsWith("PointerOver") ? hover : key.EndsWith("Pressed") ? pressed : fill;
            button.Background = fill; button.Foreground = foreground;
        }
        // Contrast changes do not necessarily change ActualTheme in an unpackaged app.
        // Only retain the system event subscription while this button is mounted.
        void ColorsChanged(UISettings _, object args) => button.DispatcherQueue.TryEnqueue(Update);
        button.Loaded += (_, _) => { Update(); brandSystemColors.ColorValuesChanged -= ColorsChanged; brandSystemColors.ColorValuesChanged += ColorsChanged; };
        button.Unloaded += (_, _) => brandSystemColors.ColorValuesChanged -= ColorsChanged;
        button.ActualThemeChanged += (_, _) => Update();
        // The brand fill is set directly, so make the disabled state unmistakable as well.
        void Dim() => button.Opacity = button.IsEnabled ? 1 : .45;
        button.IsEnabledChanged += (_, _) => Dim();
        button.Loaded += (_, _) => Dim();
        Dim();
    }

    // Scope the overrides on the control before its native template is created.
    // An application-level brush can be shadowed by WinUI's merged resources.
    static T BrandControl<T>(T control) where T : FrameworkElement
    {
        string[] fills = ["AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush", "ToggleButtonBackgroundChecked", "ToggleButtonBackgroundCheckedPointerOver", "ToggleButtonBackgroundCheckedPressed", "NavigationViewSelectionIndicatorForeground", "ListViewItemSelectionIndicatorBrush", "ProgressBarForeground", "ProgressRingForegroundThemeBrush", "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundStrokeChecked", "CheckBoxCheckBackgroundFillIndeterminate", "CheckBoxCheckBackgroundStrokeIndeterminate", "TextControlBorderBrushFocused"];
        string[] foregrounds = ["ToggleButtonForegroundChecked", "ToggleButtonForegroundCheckedPointerOver", "ToggleButtonForegroundCheckedPressed", "CheckBoxCheckGlyphForegroundChecked"];
        foreach (var (theme, fill, foreground) in new[] { ("Light", "#006B58", "#FFFFFF"), ("Default", "#76E4C0", "#102C2B") })
        {
            var dictionary = new ResourceDictionary();
            foreach (var key in fills.Concat(foregrounds))
            {
                var color = foregrounds.Contains(key) ? foreground : fill;
                dictionary[key] = (SolidColorBrush)Microsoft.UI.Xaml.Markup.XamlReader.Load($"<SolidColorBrush xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Color='{color}'/>");
            }
            control.Resources.ThemeDictionaries[theme] = dictionary;
        }
        // Let the native high-contrast template use Windows' selected colors.
        control.Resources.ThemeDictionaries["HighContrast"] = new ResourceDictionary();
        return control;
    }

    // Mascot art shipped in Assets/Brand/mascot: a transparent still and a transparent GIF per mood.
    static readonly Dictionary<string, string> MascotFiles = new()
    {
        ["you-got-this"] = "01-you-got-this", ["let-me-think"] = "02-let-me-think", ["that-worked"] = "03-that-worked",
        ["wait-a-second"] = "04-wait-a-second", ["im-listening"] = "05-im-listening", ["oh-wow"] = "06-oh-wow",
        ["try-again"] = "07-try-again", ["thank-you"] = "08-thank-you"
    };
    static readonly Dictionary<string, string> MascotAlt = new()
    {
        ["you-got-this"] = "Spring cheering you on", ["let-me-think"] = "Spring thinking it over", ["that-worked"] = "Spring celebrating",
        ["wait-a-second"] = "Spring asking you to wait a second", ["im-listening"] = "Spring listening", ["oh-wow"] = "Spring looking amazed",
        ["try-again"] = "Spring encouraging another try", ["thank-you"] = "Spring saying thank you"
    };

    static BitmapImage BrandImage(string relative, int decodeWidth = 0)
    {
        var image = new BitmapImage(new Uri("ms-appx:///Assets/Brand/" + relative));
        // Decode near the displayed size; the source art is 512px and larger.
        if (decodeWidth > 0) { image.DecodePixelType = DecodePixelType.Logical; image.DecodePixelWidth = decodeWidth; }
        return image;
    }

    // The transparent light-mode still is used in both themes; it has no backdrop of its own.
    static string MascotStill(string name) => $"mascot/{MascotFiles[name]}-light.png";

    /// <summary>
    /// Spring in one of its moods, as a transparent still;
    /// <see cref="PlayMascot"/> can briefly swap in the animation.
    /// </summary>
    static Image Mascot(string name, double size)
    {
        var image = new Image { Width = size, Height = size, Stretch = Stretch.Uniform, Tag = name };
        image.Source = BrandImage(MascotStill(name), (int)size);
        AutomationProperties.SetName(image, MascotAlt[name]);
        return image;
    }

    /// <summary>
    /// Changes the mood shown by an image created with <see cref="Mascot"/>. When <paramref name="animate"/>
    /// is set and Windows animations are enabled, the transparent GIF plays for a few seconds, then the still returns.
    /// </summary>
    static void PlayMascot(Image image, string name, bool animate = false)
    {
        image.Tag = name;
        AutomationProperties.SetName(image, MascotAlt[name]);
        int width = (int)image.Width;
        if (!animate || !AnimationsEnabled()) { image.Source = BrandImage(MascotStill(name), width); return; }
        image.Source = BrandImage($"mascot/{MascotFiles[name]}-transparent.gif", width);
        var timer = image.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(3.5);
        timer.IsRepeating = false;
        // Only settle if no newer mood replaced this one in the meantime.
        timer.Tick += (_, _) => { if ((string)image.Tag == name) image.Source = BrandImage(MascotStill(name), width); };
        timer.Start();
    }

    /// <summary>One character moment plus a plain statement of what is missing and what to do next.</summary>
    static (FrameworkElement View, Image Art, TextBlock Title, TextBlock Body) EmptyState(string mood, string title, string body, params UIElement[] actions)
    {
        var art = Mascot(mood, 132);
        var heading = Heading(title, 20);
        heading.TextAlignment = TextAlignment.Center;
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        var detail = Muted(body, 13.5);
        detail.TextAlignment = TextAlignment.Center;
        detail.HorizontalAlignment = HorizontalAlignment.Center;
        detail.MaxWidth = 520;
        var panel = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(12, 20, 12, 20), Children = { art, heading, detail } };
        if (actions.Length > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var action in actions) row.Children.Add(action);
            panel.Children.Add(row);
        }
        return (panel, art, heading, detail);
    }

    /// <summary>
    /// Title-bar lockup: Spring is the logo, next to the approved outlined wordmark, sized to sit inside the
    /// 48 px caption area (MainWindow.Chrome.cs) like the app name in Windows Settings.
    /// </summary>
    FrameworkElement BrandHeader()
    {
        var badge = new Image { Width = 32, Height = 32, Stretch = Stretch.Uniform, Source = BrandImage("mascot.png", 64), VerticalAlignment = VerticalAlignment.Center };
        // The wordmark is outlined artwork (932x276); never retype it.
        var wordmark = new Image { Width = 78, Height = 23, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        var lockup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(2, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center, Children = { badge, wordmark } };
        AutomationProperties.SetName(wordmark, "AI Gym");
        AutomationProperties.SetName(badge, "Spring, the AI Gym mascot");
        ToolTipService.SetToolTip(lockup, "AI Gym · Practice makes possibility.");

        void Update()
        {
            // Brand resources, the window material and the caption buttons follow the theme (MainWindow.Chrome.cs).
            ApplyTheme();
            bool contrast = Fluent.HighContrast;
            var systemBackground = brandSystemColors.GetColorValue(UIColorType.Background);
            bool dark = contrast ? systemBackground.R + systemBackground.G + systemBackground.B < 384 : root.ActualTheme == ElementTheme.Dark;
            wordmark.Source = BrandImage(dark ? "wordmark-dark.png" : "wordmark-light.png", 156);
        }
        root.ActualThemeChanged += (_, _) => Update();
        root.Loaded += (_, _) => Update();
        // Contrast themes are followed through the system colour change (AccessibilitySettings.HighContrastChanged is unavailable unpackaged).
        brandSystemColors.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(Update);
        try { AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", "app.ico")); } catch { }
        Update();
        return lockup;
    }
}

using System.Data;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Pickers;

namespace AiGym;

public sealed class ChartView : Grid
{
    readonly ComboBox kind = Choice("Chart", ChartModel.Kinds), x = Choice("X", []), split = Choice("Split series", []),
        aggregate = Choice("Aggregate", ["Sum", "Average", "Count", "Raw"]), axis = Choice("X axis", ["Auto", "Category", "Numeric", "Time"]),
        sort = Choice("Order", ["X ascending", "X descending", "Result order"]), palette = Choice("Colors", ["Warm", "Ocean", "Gym"]);
    readonly TextBox title = new() { Header = "Title", Width = 180, MaxLength = 100 }, unit = new() { Header = "Unit", Width = 90, MaxLength = 24 };
    readonly CheckBox legend = new() { Content = "Legend", IsChecked = true };
    readonly StackPanel measures = new() { Orientation = Orientation.Horizontal, Spacing = 12 }, toggles = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
    readonly Canvas plot = new() { Width = 1000, Height = 500, Background = Brush("#17212d") };
    readonly TextBlock caption = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(8) };
    readonly ScrollViewer scroll;
    readonly ScrollViewer toolbarSurface;
    readonly Button compactTools=new(){Content="Chart tools",HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top,Visibility=Visibility.Collapsed};
    bool compact;
    public bool Telemetry { get; set; }
    /// <summary>The caption under the chart: what the plotted rows are and how they were measured.</summary>
    public string? CaptionOverride { get; set; }
    public bool Compact {get=>compact;set{compact=value;toolbarSurface.Visibility=value?Visibility.Collapsed:Visibility.Visible;toolbarSurface.Margin=value?new Thickness(0,0,90,0):new Thickness(0);compactTools.Visibility=value?Visibility.Visible:Visibility.Collapsed;}}
    readonly HashSet<string> hidden = [];
    double zoomScale = 1; bool exporting;
    DataTable? table; bool loading; bool truncated; int loadedRows; string? unavailable;
    public ChartDefinition Definition { get; private set; }
    public event Action? Changed;
    public IntPtr WindowHandle { get; set; }
    public ChartScene? Scene { get; private set; }
    public ChartDataset? Dataset { get; private set; }
    public ChartView(ChartDefinition definition)
    {
        Definition = definition;
        ActualThemeChanged+=(_,_)=>Draw();
        void Recolor(Windows.UI.ViewManagement.UISettings _, object __) => DispatcherQueue.TryEnqueue(Draw);
        Loaded += (_, _) => { systemColors.ColorValuesChanged -= Recolor; systemColors.ColorValuesChanged += Recolor; };
        Unloaded += (_, _) => systemColors.ColorValuesChanged -= Recolor;
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); RowDefinitions.Add(new() { Height = GridLength.Auto });
        var settings = new StackPanel { Spacing = 6, Margin = new Thickness(8) };
        var mappings = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var c in new Control[] { x, split, aggregate, axis, sort }) mappings.Children.Add(c);
        settings.Children.Add(mappings); settings.Children.Add(measures);
        var appearance = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var c in new Control[] { title, unit, palette, legend }) appearance.Children.Add(c);
        var png = new Button { Content = "Export PNG" }; var svg = new Button { Content = "Export SVG" };
        png.Click += async (_, _) => await Export(false); svg.Click += async (_, _) => await Export(true);
        var zoom = new ComboBox { Header = "Zoom", ItemsSource = new[] { "100%", "150%", "200%", "300%" }, SelectedIndex = 0, Width = 90 };
        zoom.SelectionChanged += (_, _) => { zoomScale = new[] { 1d, 1.5, 2, 3 }[zoom.SelectedIndex]; Fit(); };
        settings.Children.Add(appearance);
        var configure = new Button { Content = "Chart settings", Flyout = Fluent.Acrylic(new Flyout { Content = new ScrollViewer { Content = settings, MaxWidth = 760, MaxHeight = 360, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto } }) };
        kind.Header = null; zoom.Header = null;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(kind, "Chart type");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(zoom, "Chart zoom");
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(4) };
        foreach (var c in new Control[] { kind, configure, png, svg, zoom }) toolbar.Children.Add(c);
        toolbarSurface=new ScrollViewer { Content = toolbar, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };Children.Add(toolbarSurface);Children.Add(compactTools);
        compactTools.Click+=(_,_)=>toolbarSurface.Visibility=toolbarSurface.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;
        settings.Children.Add(new TextBlock { Text = "Visible series", Margin = new Thickness(0, 6, 0, 0) });
        settings.Children.Add(new ScrollViewer { Content = toggles, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        scroll = new ScrollViewer { Content = plot, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.SizeChanged += (_, _) => Fit();
        // Both scroll areas can take keyboard focus; without names a screen reader announces only "pane".
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toolbarSurface, "Chart toolbar");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(scroll, "Chart · " + (definition.Title.Length > 0 ? definition.Title : definition.Kind));
        SetRow(scroll, 2); Children.Add(scroll); SetRow(caption, 3); Children.Add(caption);
        loading = true;
        kind.SelectedItem = definition.Kind; aggregate.SelectedItem = definition.Aggregate; axis.SelectedItem = definition.Axis; sort.SelectedItem = definition.Sort;
        palette.SelectedItem = definition.Palette; title.Text = definition.Title; unit.Text = definition.Unit; legend.IsChecked = definition.Legend;
        loading = false;
        foreach (var c in new[] { kind, x, split, aggregate, axis, sort, palette }) c.SelectionChanged += (_, _) =>
        {
            if (loading) return;
            if (c == kind && kind.SelectedItem as string == "Scatter") { loading = true; aggregate.SelectedItem = "Raw"; axis.SelectedItem = "Numeric"; loading = false; }
            Save();
        };
        title.TextChanged += (_, _) => Save(); unit.TextChanged += (_, _) => Save(); legend.Checked += (_, _) => Save(); legend.Unchecked += (_, _) => Save();
        Loaded += (_, _) => Draw();
    }
    void Fit()
    {
        if (exporting) return;
        plot.Width = Math.Max(Compact ? 280 : 640, scroll.ActualWidth * zoomScale); plot.Height = Math.Max(100, scroll.ActualHeight * zoomScale); Draw();
    }
    // Contrast themes can change without changing ActualTheme in an unpackaged app; keep one listener alive.
    static readonly Windows.UI.ViewManagement.UISettings systemColors = new();
    /// <summary>
    /// The brand palette yields to the user's contrast theme: window background, window text for labels and axes,
    /// and the theme's highlight and hyperlink colours for the first two series (later series use window text).
    /// </summary>
    static ChartScene HighContrastSurface(ChartScene scene)
    {
        string Hex(Windows.UI.ViewManagement.UIElementType type) { var c = systemColors.UIElementColor(type); return $"#{c.R:X2}{c.G:X2}{c.B:X2}"; }
        string ink = Hex(Windows.UI.ViewManagement.UIElementType.WindowText);
        string[] seriesColors = [Hex(Windows.UI.ViewManagement.UIElementType.Highlight), Hex(Windows.UI.ViewManagement.UIElementType.Hotlight)];
        // Ink and grid colours from the dark chart surface; everything else drawn in colour is a series.
        var structure = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "#DCE5EF", "#384758", "#3F5C58", "#E6F0EC" };
        var series = scene.Marks.Where(m => m.Kind != "Text" && !structure.Contains(m.Color)).Select(m => m.Color).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string Map(ChartMark m) => m.Kind == "Text" || structure.Contains(m.Color) ? ink : series.IndexOf(m.Color) is var i and >= 0 and < 2 ? seriesColors[i] : ink;
        return scene with { Background = Hex(Windows.UI.ViewManagement.UIElementType.Window), Marks = scene.Marks.Select(m => m with { Color = Map(m) }).ToList() };
    }
    static ComboBox Choice(string header, string[] items) => new() { Header = header, ItemsSource = items, MinWidth = 110 };
    static SolidColorBrush Brush(string hex) => new(Windows.UI.Color.FromArgb(255, Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16)));
    public void Bind(DataTable? data, bool isTruncated = false, int totalLoaded = 0)
    {
        table = data; truncated = isTruncated; loadedRows = totalLoaded; unavailable = data == null ? "This chart has no data to show." : null;
        loading = true;
        var names = data?.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray() ?? [];
        x.ItemsSource = names.Concat(new[] { Definition.X }).Where(n => n.Length > 0).Distinct().ToArray(); x.SelectedItem = Definition.X;
        split.ItemsSource = new[] { "(none)" }.Concat(names).Concat(Definition.Split.Length > 0 ? new[] { Definition.Split } : []).Distinct().ToArray(); split.SelectedItem = Definition.Split.Length == 0 ? "(none)" : Definition.Split;
        measures.Children.Clear(); measures.Children.Add(new TextBlock { Text = "Values:", VerticalAlignment = VerticalAlignment.Center });
        foreach (var name in names.Concat(Definition.Values ?? []).Distinct())
        {
            var check = new CheckBox { Content = name, IsChecked = Definition.Values?.Contains(name) == true };
            check.Checked += (_, _) => Save(); check.Unchecked += (_, _) => Save(); measures.Children.Add(check);
        }
        loading = false; Draw();
    }
    void Save()
    {
        if (loading) return;
        Definition = Definition with { Kind = kind.SelectedItem as string ?? Definition.Kind, X = x.SelectedItem as string ?? Definition.X,
            Values = measures.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Content).ToArray(),
            Split = split.SelectedItem is string s && s != "(none)" ? s : "", Aggregate = aggregate.SelectedItem as string ?? Definition.Aggregate,
            Axis = axis.SelectedItem as string ?? Definition.Axis, Sort = sort.SelectedItem as string ?? Definition.Sort,
            Title = title.Text, Unit = unit.Text, Palette = palette.SelectedItem as string ?? Definition.Palette, Legend = legend.IsChecked == true };
        hidden.Clear(); Changed?.Invoke(); Draw();
    }
    void Draw()
    {
        // Hidden output tabs retain their bindings; materialize native marks only on display.
        if (!IsLoaded) return;
        Dataset = table == null ? new([], "Category", unavailable ?? "No chart data yet.", 0) : ChartModel.Build(table, Definition);
        Dataset = Dataset with { Scope = $"Result {Definition.ResultSet + 1} · {Dataset.Rows:N0} visible / {loadedRows:N0} loaded" + (truncated ? " · Query display cap reached: totals may be incomplete" : "") };
        Scene = ChartLayout.Build(Definition, Dataset, plot.Width, Math.Max(plot.Height, Definition.Kind == "Big number" ? 180 + Dataset.Series.Count / 3 * 100 : plot.Height), hidden, Telemetry, Compact);
        if(new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)Scene=HighContrastSurface(Scene);
        else if(ActualTheme==ElementTheme.Light)Scene=Scene.LightSurface();
        // The Gym palette sits on the brand card surface instead of the default chart panel.
        else if(Definition.Palette=="Gym")Scene=Scene with{Background="#153332"};
        plot.Background=Brush(Scene.Background);
        plot.Height = Scene.Height; plot.Children.Clear();
        foreach (var m in Scene.Marks)
        {
            FrameworkElement element;
            if (m.Kind == "Text") element = new TextBlock { Text = m.Text, Foreground = Brush(m.Color), FontSize = m.Size, MaxWidth = Math.Max(20, Scene.Width - m.X - 8), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
            else if (m.Kind is "Rect" or "Dot") element = m.Kind == "Rect" ? new Rectangle { Width = m.W, Height = m.H, Fill = Brush(m.Color) } : new Ellipse { Width = m.W, Height = m.H, Fill = Brush(m.Color) };
            else
            {
                var path = (Microsoft.UI.Xaml.Shapes.Path)XamlReader.Load($"<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='{m.Path}'/>");
                if (m.Kind == "Line") { path.Stroke = Brush(m.Color); path.StrokeThickness = 2; } else { path.Fill = Brush(m.Color); path.Opacity = .7; }
                element = path;
            }
            Canvas.SetLeft(element, m.X); Canvas.SetTop(element, m.Y);
            if (m.Tip.Length > 0) { ToolTipService.SetToolTip(element, m.Tip); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, m.Tip); }
            plot.Children.Add(element);
        }
        toggles.Children.Clear();
        foreach (var series in Dataset.Series)
        {
            var toggle = new CheckBox { Content = series.Name, IsChecked = !hidden.Contains(series.Name) };
            toggle.Checked += (_, _) => { hidden.Remove(series.Name); Draw(); }; toggle.Unchecked += (_, _) => { hidden.Add(series.Name); Draw(); }; toggles.Children.Add(toggle);
        }
        caption.Text = CaptionOverride ?? (Telemetry ? $"{Dataset.Rows:N0} retained optimizer observations · gaps indicate no held-out measurement" : $"Result {Definition.ResultSet + 1} · {Dataset.Rows:N0} visible / {loadedRows:N0} loaded rows · loaded rows only" +
            (truncated ? " · Query display cap reached: totals may be incomplete" : "") +
            (Dataset.Skipped > 0 ? $" · {Dataset.Skipped} nonnumeric values skipped" : "") + " · NULL measures are gaps; NULL X omitted · Count counts rows" +
            (Dataset.Valid ? "" : " · " + Dataset.Message));
    }
    async Task Export(bool svg)
    {
        if (exporting) return;
        if (Dataset?.Valid != true || Scene == null) { caption.Text = "Resolve chart mappings before exporting."; return; }
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = "chart" };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            picker.FileTypeChoices.Add(svg ? "SVG" : "PNG", new[] { svg ? ".svg" : ".png" });
            var file = await picker.PickSaveFileAsync(); if (file == null) return;
            double previousWidth = plot.Width, previousHeight = plot.Height;
            exporting = true;
            try
            {
                // Reports retain their annotations even when the native pane uses compact axes.
                plot.Width = Math.Max(1000, previousWidth); plot.Height = Math.Max(500, previousHeight); Draw(); plot.UpdateLayout();
                if (svg) await Windows.Storage.FileIO.WriteTextAsync(file, Scene!.Svg());
                else
                {
                    var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(plot);
                    var pixels = await bitmap.GetPixelsAsync();
                    using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite); stream.Size = 0;
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
                    await encoder.FlushAsync();
                }
            }
            finally { plot.Width = previousWidth; plot.Height = previousHeight; exporting = false; Draw(); }
            caption.Text = "Exported " + file.Name + " · visible series · loaded rows only";
        }
        catch (Exception ex) { caption.Text = "Export failed: " + ex.Message; }
    }
}

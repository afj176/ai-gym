using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Data;

namespace AiGym;

public sealed partial class MainWindow
{
    Action<int>? selectResources;
    Action<int>? selectModelView;
    string currentRoute = "Home";
    Func<bool>? modelScanBusy;
    Action? cancelModelScan;
    static Border CardFrame(FrameworkElement child)
    {
        var card=(Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource GymSurfaceBrush}' BorderBrush='{ThemeResource GymBorderBrush}' BorderThickness='1' CornerRadius='20' Padding='18'/>");
        card.Child=child;
        // Brand radii: 20 for page cards, 8 for compact rows. Callers that set their own radius keep it.
        card.SizeChanged+=(_,e)=>
        {
            double current=card.CornerRadius.TopLeft;
            if(current is not (8 or 20)||card.CornerRadius.BottomRight!=current)return;
            double next=e.NewSize.Height<96?8:20;
            if(next!=current)card.CornerRadius=new CornerRadius(next);
        };
        return card;
    }
    static ScrollViewer VerticalScroll(FrameworkElement child)=>new() { Content=child,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
    static FrameworkElement InspectorLayout(FrameworkElement main,FrameworkElement inspector,string title)
    {
        var grid=new Grid {ColumnSpacing=12,RowSpacing=10};
        grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(400)});
        grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var caption=Display(Text(title,17));
        var card=CardFrame(Vertical(caption,inspector));card.Padding=new Thickness(16,12,16,12);
        grid.Children.Add(main);grid.Children.Add(card);Grid.SetColumn(card,1);
        bool compact=false,focused=false;
        var toggle=Button("Show "+title,(_,_)=>{});Grid.SetRow(toggle,1);Grid.SetColumnSpan(toggle,2);grid.Children.Add(toggle);
        void Arrange(){compact=grid.ActualWidth<880;grid.ColumnDefinitions[1].Width=compact?new GridLength(0):new GridLength(Math.Clamp(grid.ActualWidth*.30,340,410));Grid.SetColumn(card,compact?0:1);card.Visibility=!compact||focused?Visibility.Visible:Visibility.Collapsed;main.Visibility=compact&&focused?Visibility.Collapsed:Visibility.Visible;toggle.Visibility=compact?Visibility.Visible:Visibility.Collapsed;toggle.Content=focused?"Back to work":"Show "+title;}
        toggle.Click+=(_,_)=>{focused=!focused;Arrange();};grid.SizeChanged+=(_,_)=>Arrange();
        return grid;
    }
    static Grid FormGrid(params FrameworkElement[] elements)
        => FormGrid(680,elements);
    static Grid FormGrid(double narrowWidth,params FrameworkElement[] elements)
    {
        var grid=new Grid{ColumnSpacing=16,RowSpacing=10};grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        foreach(var element in elements)grid.Children.Add(element);
        void Arrange(){bool narrow=grid.ActualWidth<narrowWidth;grid.RowDefinitions.Clear();for(int i=0;i<(narrow?elements.Length:(elements.Length+1)/2);i++)grid.RowDefinitions.Add(new(){Height=GridLength.Auto});for(int i=0;i<elements.Length;i++){Grid.SetRow(elements[i],narrow?i:i/2);Grid.SetColumn(elements[i],narrow?0:i%2);Grid.SetColumnSpan(elements[i],narrow?2:1);}}
        grid.SizeChanged+=(_,_)=>Arrange();Arrange();return grid;
    }
    static ViewSwitcher ModeSwitcher(params (string Label,FrameworkElement Page)[] items)
    {
        var views=new ViewSwitcher {UseSegments=true};foreach(var item in items)views.Items.Add(new(){Header=item.Label,Content=item.Page});return views;
    }
    static (FrameworkElement View,Action<int> Set,Action<int,string> Label) StageRail(params string[] labels)
    {
        var rail = new Grid { ColumnSpacing = 10 };
        var entries = new List<(Border Dot, TextBlock Number, TextBlock Label)>();
        var links = new List<Border>();
        foreach (var (label, index) in labels.Select((label, index) => (label, index)))
        {
            if (index > 0)
            {
                rail.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                var line = new Border { Height = 2, MinWidth = 18, CornerRadius = new CornerRadius(1), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(line, rail.ColumnDefinitions.Count - 1);
                rail.Children.Add(line);
                links.Add(line);
            }
            rail.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var number = new TextBlock { Text = (index + 1).ToString(), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var dot = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1.5), Child = number };
            var text = Text(label, 12);
            text.VerticalAlignment = VerticalAlignment.Center;
            entries.Add((dot, number, text));
            var item = Row(dot, text);
            Grid.SetColumn(item, rail.ColumnDefinitions.Count - 1);
            rail.Children.Add(item);
        }
        int stage = 0;
        void Set(int current)
        {
            stage = current;
            // Brand tokens per theme; in high contrast every role comes from the Windows contrast theme.
            bool contrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast, dark = rail.ActualTheme == ElementTheme.Dark;
            var system = new Windows.UI.ViewManagement.UISettings();
            var accent = contrast ? system.UIElementColor(Windows.UI.ViewManagement.UIElementType.Highlight) : dark ? Windows.UI.Color.FromArgb(255, 118, 228, 192) : Windows.UI.Color.FromArgb(255, 0, 107, 88);
            var onAccent = contrast ? system.UIElementColor(Windows.UI.ViewManagement.UIElementType.HighlightText) : dark ? Windows.UI.Color.FromArgb(255, 11, 32, 32) : Microsoft.UI.Colors.White;
            var ink = contrast ? system.UIElementColor(Windows.UI.ViewManagement.UIElementType.WindowText) : dark ? Windows.UI.Color.FromArgb(255, 244, 248, 246) : Windows.UI.Color.FromArgb(255, 16, 44, 43);
            var quiet = contrast ? ink : dark ? Windows.UI.Color.FromArgb(96, 183, 210, 203) : Windows.UI.Color.FromArgb(120, 102, 127, 121);
            foreach (var (entry, index) in entries.Select((entry, index) => (entry, index)))
            {
                bool reached = index <= current;
                entry.Number.Text = index < current ? "\u2713" : (index + 1).ToString();
                entry.Number.Foreground = new SolidColorBrush(reached ? onAccent : ink);
                entry.Label.FontWeight = index == current ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
                entry.Dot.Opacity = index > current ? .7 : 1;
                entry.Dot.Background = new SolidColorBrush(reached ? accent : Microsoft.UI.Colors.Transparent);
                entry.Dot.BorderBrush = new SolidColorBrush(reached ? accent : quiet);
                AutomationProperties.SetName(entry.Dot, labels[index] + (index < current ? " \u00b7 completed stage" : index == current ? " \u00b7 current stage" : " \u00b7 upcoming"));
            }
            foreach (var (line, index) in links.Select((line, index) => (line, index))) line.Background = new SolidColorBrush(index < current ? accent : quiet);
        }
        rail.SizeChanged += (_, _) => { foreach (var entry in entries) entry.Label.Visibility = rail.ActualWidth < labels.Length * 105 ? Visibility.Collapsed : Visibility.Visible; };
        rail.Loaded += (_, _) => Set(stage);
        rail.ActualThemeChanged += (_, _) => Set(stage);
        var frame = CardFrame(rail);
        frame.Padding = new Thickness(12, 10, 12, 10);
        Set(0);
        return (frame, Set, (index, label) => entries[index].Label.Text = label);
    }
    static FrameworkElement PageLayout(string title,string description,FrameworkElement body)
    {
        var name=Heading(title,30);
        var breadcrumb=Eyebrow("AI Gym  /  "+title);breadcrumb.Visibility=Visibility.Collapsed;var descriptionText=Muted(description,14);descriptionText.MaxLines=2;descriptionText.TextTrimming=TextTrimming.CharacterEllipsis;
        var titleRow=new Grid{ColumnSpacing=16,RowSpacing=8};titleRow.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});titleRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});titleRow.RowDefinitions.Add(new(){Height=GridLength.Auto});titleRow.RowDefinitions.Add(new(){Height=GridLength.Auto});titleRow.Children.Add(name);
        var header=Stack(breadcrumb,titleRow,descriptionText);header.Spacing=4;header.Margin=new Thickness(2,4,2,6);
        header.SizeChanged+=(_,_)=>{bool compact=header.ActualWidth<880;descriptionText.Visibility=string.IsNullOrEmpty(descriptionText.Text)?Visibility.Collapsed:Visibility.Visible;name.FontSize=compact?25:30;};
        return Vertical(header,body);
    }
    void BuildNavigation()
    {
        NavigationViewItem Add(string key,string label,string glyph,bool footer=false)
        {
            var item=new NavigationViewItem{Content=label,Tag=key,Icon=new FontIcon{Glyph=glyph},Height=40,MinHeight=40,FontSize=14};
            BrandControl(item);
            ToolTipService.SetToolTip(item,label);
            if(footer)nav.FooterMenuItems.Add(item);else nav.MenuItems.Add(item);
            return item;
        }
        void Group(string label)=>nav.MenuItems.Add(new NavigationViewItemHeader{Content=label.ToUpperInvariant(),FontSize=11,CharacterSpacing=90});
        // Order follows the practice loop: dataset, train, compare.
        Add("Home","Home","");
        Group("Practice loop");
        Add("Datasets","Datasets","");Add("Training","Training lab","");Add("Compare","Compare models","");
        Group("Explore");
        Add("Agent lab","Agent lab","");
        Add("Models and connections","Models & connections","",true);
    }
    void ApplyTheme()
    {
        string[] keys=["AccentFillColorDefaultBrush","AccentFillColorSecondaryBrush","AccentFillColorTertiaryBrush","NavigationViewSelectionIndicatorForeground","ToggleButtonBackgroundChecked","ToggleButtonBackgroundCheckedPointerOver","ToggleButtonBackgroundCheckedPressed","TextFillColorPrimaryBrush"];
        // The window background itself is set by ApplyWindowMaterial, so Mica can show through the sidebar.
        bool dark=root.ActualTheme==ElementTheme.Dark;
        ApplyWindowMaterial(dark);
        if(new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast){foreach(var key in keys)root.Resources.Remove(key);return;}
        var accent=dark?Windows.UI.Color.FromArgb(255,118, 228, 192):Windows.UI.Color.FromArgb(255,0, 107, 88);
        var ink=dark?Windows.UI.Color.FromArgb(255,244,248,246):Windows.UI.Color.FromArgb(255,16,44,43);
        foreach(var key in keys){var color=key=="TextFillColorPrimaryBrush"?ink:accent;if(root.Resources.TryGetValue(key,out var prior)&&prior is SolidColorBrush brush)brush.Color=color;else root.Resources[key]=new SolidColorBrush(color);}
    }
}

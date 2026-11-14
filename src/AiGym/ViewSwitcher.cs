using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;

namespace AiGym;

// Page modes are a native menu, not document tabs. All content stays mounted so
// changing modes preserves editors, scroll position and in-flight work.
public sealed record ViewOption { public string Header { get; init; } = "View"; public FrameworkElement? Content { get; init; } }

public sealed class ViewSwitcher : UserControl
{
    readonly DropDownButton selector = new() { HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14,9,14,9) };
    readonly MenuFlyout menu = new() { Placement=Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
    readonly Grid host = new();
    readonly StackPanel segments = new() { Orientation=Orientation.Horizontal, Spacing=2 };
    readonly ScrollViewer segmentScroll;
    bool useSegments;
    bool pills;
    /// <summary>Secondary tab rows use filled pills; primary rows keep the underline.</summary>
    public bool Pills { get => pills; set { pills = value; Render(); } }
    public bool UseSegments { get => useSegments; set { useSegments=value; Render(); } }
    int selected = -1;
    public ObservableCollection<ViewOption> Items { get; } = [];
    public event EventHandler? SelectionChanged;
    public int SelectedIndex
    {
        get => selected;
        set
        {
            int next = Items.Count == 0 ? -1 : Math.Clamp(value,0,Items.Count-1);
            if (next == selected) return;
            bool shown = selected >= 0;
            selected = next; Render(); SelectionChanged?.Invoke(this,EventArgs.Empty);
            // Switching modes settles the new view into place; the first selection appears with its page.
            if (shown && selected >= 0 && Items[selected].Content is FrameworkElement view) Fluent.Appear(view, 8);
        }
    }
    public ViewSwitcher()
    {
        var layout=new Grid {RowSpacing=8}; Content=layout;
        layout.RowDefinitions.Add(new() { Height=GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height=new GridLength(1,GridUnitType.Star) });
        selector.Flyout=Fluent.Acrylic(menu); layout.Children.Add(selector); Grid.SetRow(host,1); layout.Children.Add(host);
        segmentScroll=new ScrollViewer {Content=segments,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled,Visibility=Visibility.Collapsed};
        layout.Children.Add(segmentScroll);
        ToolTipService.SetToolTip(selector,"Change view");
        Items.CollectionChanged+=(_,_)=>
        {
            host.Children.Clear();
            foreach(var item in Items)
                if(item.Content is FrameworkElement content)host.Children.Add(content);
            if(selected<0&&Items.Count>0)SelectedIndex=0;
            else { if(selected>=Items.Count)SelectedIndex=Items.Count-1; Render(); }
            host.Visibility=host.Children.Count==0?Visibility.Collapsed:Visibility.Visible;
            layout.RowDefinitions[1].Height=host.Children.Count==0?GridLength.Auto:new GridLength(1,GridUnitType.Star);
        };
        foreach(bool reverse in new[]{false,true})
        {
            var shortcut=new KeyboardAccelerator {Key=VirtualKey.Tab,Modifiers=VirtualKeyModifiers.Control|(reverse?VirtualKeyModifiers.Shift:VirtualKeyModifiers.None)};
            shortcut.Invoked+=(_,e)=>{if(Items.Count>1){SelectedIndex=(selected+(reverse?Items.Count-1:1))%Items.Count;e.Handled=true;FocusSelector();}};
            KeyboardAccelerators.Add(shortcut);
        }
    }
    public void FocusSelector(){if(useSegments&&selected>=0&&selected<segments.Children.Count)((Control)segments.Children[selected]).Focus(FocusState.Keyboard);else selector.Focus(FocusState.Keyboard);}
    void Render()
    {
        selector.Visibility=useSegments?Visibility.Collapsed:Visibility.Visible;
        segmentScroll.Visibility=useSegments?Visibility.Visible:Visibility.Collapsed;
        segments.Children.Clear();
        menu.Items.Clear();
        for(int i=0;i<Items.Count;i++)
        {
            var item=Items[i]; int index=i;
            string label=Convert.ToString(item.Header)??"View";
            var option=new RadioMenuFlyoutItem { Text=label,IsChecked=i==selected };
            option.Click+=(_,_)=>SelectedIndex=index;menu.Items.Add(option);
            var segment=new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton {Content=label,IsChecked=i==selected,Padding=pills?new Thickness(14,6,14,7):new Thickness(14,8,14,8),FontSize=pills?13:15};
            void Colors()
            {
                // Brand tokens: accent marks the selected view, ink the others; a soft tint gives hover and pressed feedback.
                string[] rest=["ToggleButtonBackground","ToggleButtonBackgroundChecked","ToggleButtonBackgroundCheckedPointerOver","ToggleButtonBackgroundCheckedPressed"];
                string[] hover=["ToggleButtonBackgroundPointerOver","ToggleButtonBackgroundPressed"];
                string[] foregrounds=["ToggleButtonForeground","ToggleButtonForegroundPointerOver","ToggleButtonForegroundPressed","ToggleButtonForegroundChecked","ToggleButtonForegroundCheckedPointerOver","ToggleButtonForegroundCheckedPressed"];
                string[] borders=["ToggleButtonBorderBrush","ToggleButtonBorderBrushPointerOver","ToggleButtonBorderBrushPressed","ToggleButtonBorderBrushChecked","ToggleButtonBorderBrushCheckedPointerOver","ToggleButtonBorderBrushCheckedPressed"];
                if(new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
                {
                    // High contrast: drop every override and let the native toggle template show selection.
                    foreach(var key in rest.Concat(hover).Concat(foregrounds).Concat(borders))segment.Resources.Remove(key);
                    segment.ClearValue(Control.BackgroundProperty);segment.ClearValue(Control.ForegroundProperty);segment.ClearValue(Control.BorderBrushProperty);segment.ClearValue(Control.BorderThicknessProperty);
                    return;
                }
                bool dark=segment.ActualTheme==ElementTheme.Dark,current=index==selected;
                static Microsoft.UI.Xaml.Media.SolidColorBrush Brush(byte a,byte r,byte g,byte b)=>new(Windows.UI.Color.FromArgb(a,r,g,b));
                var accent=dark?Brush(255,118,228,192):Brush(255,0,107,88);
                var ink=dark?Brush(255,244,248,246):Brush(255,16,44,43);
                var tint=dark?Brush(41,118,228,192):Brush(31,0,107,88);
                var hoverFill=dark?Brush(22,244,248,246):Brush(16,16,44,43);
                var clear=Brush(0,0,0,0);
                var fill=pills&&current?tint:clear;
                var foreground=current?accent:ink;
                var border=!pills&&current?accent:clear;
                foreach(var key in rest)segment.Resources[key]=fill;
                foreach(var key in hover)segment.Resources[key]=pills&&current?tint:hoverFill;
                foreach(var key in foregrounds)segment.Resources[key]=foreground;
                foreach(var key in borders)segment.Resources[key]=border;
                segment.Background=fill;segment.Foreground=foreground;segment.BorderBrush=border;
                segment.BorderThickness=pills?new Thickness(0):new Thickness(0,0,0,2);
                segment.CornerRadius=new CornerRadius(pills?8:0);
                segment.FontWeight=current?Microsoft.UI.Text.FontWeights.SemiBold:Microsoft.UI.Text.FontWeights.Normal;
            }
            segment.Loaded+=(_,_)=>Colors();segment.ActualThemeChanged+=(_,_)=>Colors();Colors();
            AutomationProperties.SetName(segment,label+" view");
            segment.Click+=(_,_)=>{SelectedIndex=index;FocusSelector();};segments.Children.Add(segment);
            if(item.Content is FrameworkElement content)content.Visibility=i==selected?Visibility.Visible:Visibility.Collapsed;
        }
        string title=selected>=0?Convert.ToString(Items[selected].Header)??"View":"Choose view";
        selector.Content=new TextBlock {Text=title,FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=300};
        AutomationProperties.SetName(selector,title+" · Change view");
        selector.IsEnabled=Items.Count>1;
    }
}

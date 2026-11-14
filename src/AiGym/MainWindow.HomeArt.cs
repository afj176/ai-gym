using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace AiGym;

/// <summary>
/// The Home banner: a spring settling into rest (a damped oscillation, which is also how a training loss settles),
/// its echo, a soft ground of converging loss, checkpoint marks on each swing, and the rings of a weight plate,
/// drawn in the brand mint and teal. The curves drift more slowly than the page as it scrolls, so the banner gains
/// depth without any motion of its own. High contrast shows the plain system surface instead.
/// </summary>
public sealed partial class MainWindow
{
    static Border HomeBanner(ScrollViewer page, FrameworkElement content)
    {
        var wash = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        var washStops = new[] { new GradientStop { Offset = 0 }, new GradientStop { Offset = .55 }, new GradientStop { Offset = 1 } };
        foreach (var stop in washStops) wash.GradientStops.Add(stop);

        // The weight plate: concentric rings resting against the right edge. It stays put while the curves drift
        // (and it sits outside the parallax layer, where right-aligned elements do not render).
        var plate = new Grid { Width = 360, Height = 360, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -40, -120, -40), IsHitTestVisible = false };
        var rings = new List<(Ellipse Ring, double Weight)>();
        foreach (var (size, thickness, weight) in new[] { (360.0, 26.0, .55), (282.0, 10.0, .9), (214.0, 2.0, 1.0), (150.0, 18.0, .7), (64.0, 2.0, 1.0) })
        {
            var ring = new Ellipse { Width = size, Height = size, StrokeThickness = thickness, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            plate.Children.Add(ring); rings.Add((ring, weight));
        }

        // The drifting layer: everything is drawn from the layer's own size, so it fills any banner width.
        var canvas = new Canvas { IsHitTestVisible = false };
        var ground = new Polygon();
        var echo = new Polyline { StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
        var spring = new Polyline { StrokeThickness = 3, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        var marks = new List<Ellipse>();
        canvas.Children.Add(ground); canvas.Children.Add(echo); canvas.Children.Add(spring);
        for (int i = 0; i < 7; i++) { var mark = new Ellipse { Width = 7, Height = 7 }; marks.Add(mark); canvas.Children.Add(mark); }

        // y in [0, 1] from the top of the band; a decaying swing around a line that itself descends gently.
        static double Swing(double t, double phase) => .5 + .08 * t + .34 * Math.Exp(-3.1 * t) * Math.Cos(2 * Math.PI * 4.2 * t + phase);
        const double drift = 48;
        void Draw(Size size)
        {
            // The layer is taller than the banner by the drift; keep the art in the banner's lower strip, clear of the
            // mascot and the call to action on the left, so the text always reads on a quiet surface.
            double width = size.Width, height = size.Height - drift;
            if (width <= 0 || height <= 0) return;
            double band = Math.Clamp(height * .3, 48, 90), bottom = height + drift * .5 - 6, top = bottom - band, start = width * .3;
            var springPoints = new PointCollection(); var echoPoints = new PointCollection();
            var groundPoints = new PointCollection { new Point(0, size.Height) };
            for (int i = 0; i <= 240; i++)
            {
                double t = i / 240.0, x = start + t * (width - start);
                springPoints.Add(new Point(x, top + band * Swing(t, 0)));
                echoPoints.Add(new Point(x, top + band * Swing(t, .9) + 6));
            }
            for (int i = 0; i <= 120; i++)
            {
                // The ground: a loss that falls quickly, then flattens, across the whole banner.
                double t = i / 120.0;
                groundPoints.Add(new Point(t * width, bottom - band * (.15 + .55 * Math.Exp(-3 * t)) + 8));
            }
            groundPoints.Add(new Point(width, size.Height));
            spring.Points = springPoints; echo.Points = echoPoints; ground.Points = groundPoints;
            // A checkpoint mark at the bottom of each swing, where the spring changes direction.
            int placed = 0;
            for (int i = 1; i < 240 && placed < marks.Count; i++)
            {
                double a = springPoints[i - 1].Y, b = springPoints[i].Y, c = springPoints[i + 1].Y;
                if (b >= a && b > c)
                {
                    Canvas.SetLeft(marks[placed], springPoints[i].X - 3.5); Canvas.SetTop(marks[placed], b - 3.5);
                    marks[placed].Visibility = Visibility.Visible; placed++;
                }
            }
            for (; placed < marks.Count; placed++) marks[placed].Visibility = Visibility.Collapsed;
        }
        // A bare Canvas asks for no size, and ParallaxView then gives it none; the host grid takes the full banner.
        var host = new Grid { MinHeight = 200, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, IsHitTestVisible = false, Children = { canvas } };
        host.SizeChanged += (_, e) => Draw(e.NewSize);

        var layers = new Grid();
        layers.Children.Add(plate);
        var parallax = Fluent.Parallax(page, host, drift);
        layers.Children.Add(parallax);
        layers.Children.Add(content);
        var banner = new Border { Child = layers, CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1), Background = wash };
        AutomationProperties.SetAccessibilityView(plate, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

        void Theme()
        {
            bool contrast = Fluent.HighContrast, dark = banner.ActualTheme == ElementTheme.Dark;
            plate.Visibility = parallax.Visibility = contrast ? Visibility.Collapsed : Visibility.Visible;
            if (contrast)
            {
                // The page's high-contrast surface, outlined in the window text colour.
                banner.Background = new SolidColorBrush(brandSystemColors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background));
                banner.BorderBrush = new SolidColorBrush(brandSystemColors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground));
                return;
            }
            banner.Background = wash;
            banner.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(46, 183, 210, 203) : Color.FromArgb(64, 102, 127, 121));
            // Brand surfaces: white and mint by day, deep teal at night.
            washStops[0].Color = dark ? Color.FromArgb(255, 21, 51, 50) : Color.FromArgb(255, 255, 255, 255);
            washStops[1].Color = dark ? Color.FromArgb(255, 17, 43, 42) : Color.FromArgb(255, 241, 249, 245);
            washStops[2].Color = dark ? Color.FromArgb(255, 14, 38, 37) : Color.FromArgb(255, 226, 242, 235);
            // Mint reads on the dark surface, the deep accent on the light one; both stay faint behind the text.
            var accent = dark ? Color.FromArgb(255, 118, 228, 192) : Color.FromArgb(255, 0, 107, 88);
            Color Fade(byte alpha) => Color.FromArgb(alpha, accent.R, accent.G, accent.B);
            spring.Stroke = new SolidColorBrush(Fade(dark ? (byte)140 : (byte)112));
            echo.Stroke = new SolidColorBrush(Fade(dark ? (byte)62 : (byte)52));
            ground.Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                GradientStops = { new GradientStop { Color = Fade(dark ? (byte)40 : (byte)34), Offset = 0 }, new GradientStop { Color = Fade(0), Offset = 1 } }
            };
            foreach (var mark in marks) { mark.Fill = new SolidColorBrush(Fade(dark ? (byte)200 : (byte)150)); mark.Stroke = new SolidColorBrush(dark ? Color.FromArgb(255, 21, 51, 50) : Microsoft.UI.Colors.White); mark.StrokeThickness = 1.5; }
            foreach (var (ring, weight) in rings) ring.Stroke = new SolidColorBrush(Fade((byte)((dark ? 34 : 26) * weight)));
        }
        banner.ActualThemeChanged += (_, _) => Theme();
        banner.Loaded += (_, _) => Theme();
        Theme();
        return banner;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OverrideMirageLauncher.Core;

public static class UiAnim
{
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseInOut = new CubicEase { EasingMode = EasingMode.EaseInOut };

    private const int FadeMs = 220;
    private const int SlideMs = 280;

    public static TranslateTransform TranslateOf(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform existing) return existing;

        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }

    public static void FadeIn(FrameworkElement element, int delayMs = 0, double fromY = 8, int ms = FadeMs)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);

        if (delayMs > 0) element.Opacity = 0;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut };
        if (delayMs > 0) fade.BeginTime = TimeSpan.FromMilliseconds(delayMs);
        element.BeginAnimation(UIElement.OpacityProperty, fade);

        if (Math.Abs(fromY) > 0.1)
        {
            var transform = TranslateOf(element);
            var slide = new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut };
            if (delayMs > 0) slide.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            transform.BeginAnimation(TranslateTransform.YProperty, slide);
        }
    }

    public static void SlideInX(FrameworkElement element, double fromX, int ms = SlideMs, int delayMs = 0)
    {
        var transform = TranslateOf(element);
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.BeginAnimation(TranslateTransform.YProperty, null);

        var slide = new DoubleAnimation(fromX, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut };
        if (delayMs > 0) slide.BeginTime = TimeSpan.FromMilliseconds(delayMs);
        transform.BeginAnimation(TranslateTransform.XProperty, slide);

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut };
        if (delayMs > 0) fade.BeginTime = TimeSpan.FromMilliseconds(delayMs);
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    public static void Expand(FrameworkElement element, bool expand, int ms = 260)
    {
        element.BeginAnimation(FrameworkElement.HeightProperty, null);

        if (expand)
        {
            element.Visibility = Visibility.Visible;
            element.UpdateLayout();

            var target = element.ActualHeight;
            if (target <= 0)
            {
                element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                target = element.DesiredSize.Height;
            }

            element.Height = 0;
            var grow = new DoubleAnimation(0, target, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut };
            grow.Completed += (_, _) =>
            {
                element.BeginAnimation(FrameworkElement.HeightProperty, null);
                element.Height = double.NaN;
            };
            element.BeginAnimation(FrameworkElement.HeightProperty, grow);
        }
        else
        {
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var from = element.ActualHeight > 0 ? element.ActualHeight : element.DesiredSize.Height;
            if (from <= 0) { element.Visibility = Visibility.Collapsed; return; }

            element.Height = from;
            var shrink = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseInOut };
            shrink.Completed += (_, _) =>
            {
                element.BeginAnimation(FrameworkElement.HeightProperty, null);
                element.Height = double.NaN;
                element.Visibility = Visibility.Collapsed;
            };
            element.BeginAnimation(FrameworkElement.HeightProperty, shrink);
        }
    }

    public static void Rotate(FrameworkElement element, bool expanded, int ms = 220)
    {
        if (element.RenderTransform is not RotateTransform transform)
        {
            transform = new RotateTransform(0);
            element.RenderTransform = transform;
            element.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        var to = expanded ? 90.0 : 0.0;
        var animation = new DoubleAnimation(transform.Angle, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut };
        transform.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    public static void BackgroundTo(Border border, Color to, int ms = 160)
    {
        var from = border.Background is SolidColorBrush current ? current.Color : Colors.Transparent;

        var brush = new SolidColorBrush(from);
        border.Background = brush;

        var animation = new ColorAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    public static void ForegroundTo(TextBlock text, Color to, int ms = 180)
    {
        var from = text.Foreground is SolidColorBrush current ? current.Color : Colors.Black;

        var brush = new SolidColorBrush(from);
        text.Foreground = brush;

        var animation = new ColorAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    public static void OverlayIn(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        SlideInX(element, -16, 240);
    }

    public static void OverlayOut(FrameworkElement element, Action? after = null)
    {
        var transform = TranslateOf(element);
        transform.BeginAnimation(TranslateTransform.XProperty, null);

        var fade = new DoubleAnimation(element.Opacity, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = EaseOut };
        var slide = new DoubleAnimation(0, -12, TimeSpan.FromMilliseconds(140)) { EasingFunction = EaseOut };

        fade.Completed += (_, _) =>
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            element.Opacity = 1;
            transform.X = 0;
            element.Visibility = Visibility.Collapsed;
            after?.Invoke();
        };

        element.BeginAnimation(UIElement.OpacityProperty, fade);
        transform.BeginAnimation(TranslateTransform.XProperty, slide);
    }
}

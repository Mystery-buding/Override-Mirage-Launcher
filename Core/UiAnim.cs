using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OverrideMirageLauncher.Core;

/// <summary>
/// 界面动画小工具：淡入、横滑、展开 / 收起、旋转、背景渐变。
/// 全部用代码动画（不走 Storyboard），方便给动态生成的行加分阶段延迟。
/// </summary>
public static class UiAnim
{
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseInOut = new CubicEase { EasingMode = EasingMode.EaseInOut };

    private const int FadeMs = 220;
    private const int SlideMs = 280;

    /// <summary>取元素自带的位移变换，没有就装一个（XAML 里一般已经写好）。</summary>
    public static TranslateTransform TranslateOf(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform existing) return existing;

        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }

    /// <summary>淡入，可带纵向位移与延迟（给列表做错峰入场）。</summary>
    public static void FadeIn(FrameworkElement element, int delayMs = 0, double fromY = 8, int ms = FadeMs)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);

        // 有延迟时先把透明度压到 0，否则延迟期间会按原样闪一下
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

    /// <summary>横向滑入（正 fromX = 从右侧进来）。</summary>
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

    /// <summary>
    /// 展开 / 收起一块内容：高度从 0 到实测高度（或反过来），收起动画结束后再隐藏。
    /// 列表内容在动画期间保持可见，所以看起来是「撑开」而不是「闪现」。
    /// </summary>
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

    /// <summary>箭头旋转：0°（收起）↔ 90°（展开）。</summary>
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

    /// <summary>背景色渐变（悬停高亮用）。每次都换成元素自己的画笔实例，避免动到共享的静态画笔。</summary>
    public static void BackgroundTo(Border border, Color to, int ms = 160)
    {
        var from = border.Background is SolidColorBrush current ? current.Color : Colors.Transparent;

        var brush = new SolidColorBrush(from);
        border.Background = brush;

        var animation = new ColorAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    /// <summary>前景色渐变（标题变绿之类的状态切换），同样用独立画笔实例。</summary>
    public static void ForegroundTo(TextBlock text, Color to, int ms = 180)
    {
        var from = text.Foreground is SolidColorBrush current ? current.Color : Colors.Black;

        var brush = new SolidColorBrush(from);
        text.Foreground = brush;

        var animation = new ColorAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    /// <summary>覆盖层出现：整块淡入 + 轻微左移。</summary>
    public static void OverlayIn(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        SlideInX(element, -16, 240);
    }

    /// <summary>覆盖层消失：淡出后收起（避免瞬间消失显得生硬）。</summary>
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

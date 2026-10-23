using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverrideMirageLauncher.Core;

namespace OverrideMirageLauncher;

/// <summary>
/// 整合包图标：优先用自带 icon.png，其次加载器徽标，最后草方块。
/// 全部矢量绘制，不依赖任何图片资源。
/// </summary>
public static class ModpackIcons
{
    public static FrameworkElement Create(ModpackEntry? pack, double size)
    {
        var content = pack?.IconPath is { } path && File.Exists(path)
            ? TryLoadImage(path, size) ?? Fallback(pack, size)
            : Fallback(pack, size);

        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.2),
            ClipToBounds = true,
            Background = Brushes.Transparent,
            Child = content
        };
    }

    private static FrameworkElement? TryLoadImage(string path, double size)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = Math.Max(32, (int)Math.Ceiling(size * 2));
            bitmap.EndInit();
            bitmap.Freeze();

            return new Image
            {
                Source = bitmap,
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        catch
        {
            return null;
        }
    }

    private static FrameworkElement Fallback(ModpackEntry? pack, double size)
    {
        if (pack?.Loader is { } loader) return LoaderIcons.Create(loader, size);
        return CreateGrassBlock();
    }

    /// <summary>正式版：贴了草皮纹理的草方块。</summary>
    public static FrameworkElement CreateGrassBlock() => BlockImage(BlockArt.GrassImage);

    /// <summary>预览版：命令方块。</summary>
    public static FrameworkElement CreateCommandBlock() => BlockImage(BlockArt.CommandImage);

    /// <summary>远古版：泥土块。</summary>
    public static FrameworkElement CreateDirtBlock() => BlockImage(BlockArt.DirtImage);

    private static FrameworkElement BlockImage(ImageSource source)
    {
        var image = new Image { Source = source, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }
}

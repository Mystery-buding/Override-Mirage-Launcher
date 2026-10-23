using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverrideMirageLauncher.Core;

namespace OverrideMirageLauncher;

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

    public static FrameworkElement CreateGrassBlock() => BlockImage(BlockArt.GrassImage);

    public static FrameworkElement CreateCommandBlock() => BlockImage(BlockArt.CommandImage);

    public static FrameworkElement CreateDirtBlock() => BlockImage(BlockArt.DirtImage);

    private static FrameworkElement BlockImage(ImageSource source)
    {
        var image = new Image { Source = source, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }
}

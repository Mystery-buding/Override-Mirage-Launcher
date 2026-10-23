using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverrideMirageLauncher.Core;

namespace OverrideMirageLauncher;

/// <summary>
/// 加载器图标：Fabric / NeoForge 用 PCL 的方块素材，Forge 用铁砧（Anvil），
/// Quilt 没有现成素材，用紫底字母块兜底。
/// </summary>
public static class LoaderIcons
{
    public static FrameworkElement Create(LoaderKind kind, double size)
    {
        var asset = kind switch
        {
            LoaderKind.NeoForge => BlockArt.LoaderAsset("NeoForge"),
            LoaderKind.Forge => BlockArt.LoaderAsset("Anvil"),
            LoaderKind.Fabric => BlockArt.LoaderAsset("Fabric"),
            _ => null
        };

        FrameworkElement content;

        if (asset is not null)
        {
            var image = new Image { Source = asset, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            content = image;
        }
        else
        {
            content = Badge(kind, size);
        }

        return new Border
        {
            Width = size,
            Height = size,
            ClipToBounds = true,
            Background = Brushes.Transparent,
            Child = content
        };
    }

    private static FrameworkElement Badge(LoaderKind kind, double size)
    {
        var (label, color) = kind switch
        {
            LoaderKind.Quilt => ("Q", Color.FromRgb(0x8A, 0x4F, 0xD0)),
            LoaderKind.Fabric => ("F", Color.FromRgb(0x9A, 0x76, 0x4E)),
            LoaderKind.Forge => ("F", Color.FromRgb(0x36, 0x52, 0x6B)),
            LoaderKind.NeoForge => ("N", Color.FromRgb(0xE0, 0x5F, 0x2E)),
            _ => ("?", Color.FromRgb(0x8A, 0x92, 0xA0))
        };

        return new Border
        {
            Background = new SolidColorBrush(color),
            CornerRadius = new CornerRadius(size * 0.2),
            Child = new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = Math.Max(9, size * 0.5),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }
}

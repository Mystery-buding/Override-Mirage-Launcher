using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OverrideMirageLauncher.Core;

namespace OverrideMirageLauncher;

/// <summary>
/// 版本行图标（PCL 风格）：正式版草方块、预览版命令方块、远古版圆石。
/// 具体图片由 <see cref="BlockArt"/> 决定（自定义覆盖 → 内嵌素材 → 程序生成兜底）。
/// </summary>
public static class VersionIcons
{
    public static FrameworkElement Create(VersionSummary version, double size)
    {
        FrameworkElement content = version.Type switch
        {
            "snapshot" => ModpackIcons.CreateCommandBlock(),
            "old_alpha" or "old_beta" => ModpackIcons.CreateDirtBlock(),
            _ => ModpackIcons.CreateGrassBlock()
        };

        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.16),
            ClipToBounds = true,
            Background = Brushes.Transparent,
            Child = content
        };
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OverrideMirageLauncher.Core;

namespace OverrideMirageLauncher;

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

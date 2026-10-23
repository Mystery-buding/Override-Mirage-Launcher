using System.IO;
using System.Text.Json;

namespace OverrideMirageLauncher.Core;

/// <summary>本地整合包（versions 目录下的一项）。</summary>
/// <param name="Id">目录名 / 版本 id</param>
/// <param name="Loader">从继承链推断出的模组加载器；原版为 null</param>
/// <param name="IconPath">整合包自带图标（versions/&lt;id&gt;/icon.png 等），没有则为 null</param>
public sealed record ModpackEntry(string Id, LoaderKind? Loader, string? IconPath);

/// <summary>
/// 扫描 versions 目录，识别加载器与图标。
/// 只认「目录里有同名 json」的项，与启动器的整合包列表保持一致。
/// </summary>
public static class ModpackCatalog
{
    private static readonly string[] IconNames = { "icon.png", "icon.jpg", "icon.jpeg", "icon.webp" };

    public static List<ModpackEntry> Scan(string gameDir)
    {
        var versionsDir = Paths.VersionsDir(gameDir);
        if (!Directory.Exists(versionsDir)) return new List<ModpackEntry>();

        var found = new List<(ModpackEntry Entry, DateTime Time)>();

        foreach (var dir in Directory.GetDirectories(versionsDir))
        {
            var id = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!File.Exists(Path.Combine(dir, id + ".json"))) continue;

            try
            {
                found.Add((new ModpackEntry(id, DetectLoader(versionsDir, id), FindIcon(dir)),
                    Directory.GetLastWriteTime(dir)));
            }
            catch
            {
                // 单个整合包异常不影响其它项
            }
        }

        // 装模组加载器时会自动装一份原版做底座，别的版本 inheritsFrom 指向它。
        // 那不是用户主动装的整合包，不该出现在选择列表里（删掉它也会被解析器重新拉回来）。
        // 例外：如果这个底座自己有存档 / 模组 / 配置，说明用户真的当整合包玩过，必须继续显示，
        // 否则装完加载器后世界和模组会「跟着消失」。
        var bases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (entry, _) in found)
        {
            if (FindParentId(versionsDir, entry.Id) is not { } parent) continue;
            if (HasUserData(gameDir, parent)) continue;
            bases.Add(parent);
        }

        return found.Where(x => !bases.Contains(x.Entry.Id))
                    .OrderByDescending(x => x.Time)
                    .Select(x => x.Entry)
                    .ToList();
    }

    private static readonly string[] UserDataFolders =
    { "saves", "mods", "config", "resourcepacks", "shaderpacks", "screenshots" };

    /// <summary>某个版本有没有用户自己的内容（存档 / 模组 / 配置等）。</summary>
    private static bool HasUserData(string gameDir, string id)
    {
        var dir = Paths.InstanceDir(gameDir, id);
        if (!Directory.Exists(dir)) return false;

        foreach (var name in UserDataFolders)
        {
            var sub = Path.Combine(dir, name);
            if (!Directory.Exists(sub)) continue;
            if (Directory.EnumerateFileSystemEntries(sub).Any()) return true;
        }

        return false;
    }

    /// <summary>读版本 JSON 的 inheritsFrom，没有则返回 null。</summary>
    private static string? FindParentId(string versionsDir, string id)
    {
        var path = Path.Combine(versionsDir, id, id + ".json");
        if (!File.Exists(path)) return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("inheritsFrom", out var parent)
                ? parent.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>沿 inheritsFrom 链找加载器，改名后的整合包也能识别出来。</summary>
    private static LoaderKind? DetectLoader(string versionsDir, string id)
    {
        var current = id;

        for (var depth = 0; depth < 6 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            if (ParseLoaderName(current) is { } kind) return kind;

            var path = Path.Combine(versionsDir, current, current + ".json");
            if (!File.Exists(path)) return null;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                current = doc.RootElement.TryGetProperty("inheritsFrom", out var parent)
                    ? parent.GetString() ?? ""
                    : "";
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static LoaderKind? ParseLoaderName(string id)
    {
        if (id.Contains("neoforge", StringComparison.OrdinalIgnoreCase)) return LoaderKind.NeoForge;
        if (id.Contains("forge", StringComparison.OrdinalIgnoreCase)) return LoaderKind.Forge;
        if (id.Contains("fabric", StringComparison.OrdinalIgnoreCase)) return LoaderKind.Fabric;
        if (id.Contains("quilt", StringComparison.OrdinalIgnoreCase)) return LoaderKind.Quilt;
        return null;
    }

    private static string? FindIcon(string versionDir)
    {
        foreach (var name in IconNames)
        {
            var path = Path.Combine(versionDir, name);
            if (File.Exists(path)) return path;
        }

        return null;
    }
}

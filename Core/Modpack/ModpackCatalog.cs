using System.IO;
using System.Text.Json;

namespace OverrideMirageLauncher.Core;

public sealed record ModpackEntry(string Id, LoaderKind? Loader, string? IconPath);

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

            }
        }

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

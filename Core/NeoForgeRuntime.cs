using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OverrideMirageLauncher.Core;

public static class NeoForgeRuntime
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static async Task<bool> EnsureAsync(string gameDir, string versionId, string versionJson,
        Action<string>? log = null, CancellationToken ct = default)
    {
        if (JsonNode.Parse(versionJson) is not JsonObject node) return false;

        var mainClass = node["mainClass"]?.GetValue<string>() ?? "";
        if (!mainClass.StartsWith("net.neoforged.", StringComparison.OrdinalIgnoreCase)) return false;

        var forgeVersion = GameArgValue(node, "--fml.neoForgeVersion");
        if (forgeVersion.Length == 0 && versionId.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase))
            forgeVersion = versionId["neoforge-".Length..];

        var mcVersion = GameArgValue(node, "--fml.mcVersion");
        if (mcVersion.Length == 0) mcVersion = node["inheritsFrom"]?.GetValue<string>() ?? "";

        var neoFormVersion = GameArgValue(node, "--fml.neoFormVersion");
        if (neoFormVersion.Length == 0) neoFormVersion = "1";

        if (forgeVersion.Length == 0 || mcVersion.Length == 0) return false;

        var librariesDir = Paths.LibrariesDir(gameDir);
        var changed = false;

        var universalRel = $"net/neoforged/neoforge/{forgeVersion}/neoforge-{forgeVersion}-universal.jar";
        if (File.Exists(ToLocal(librariesDir, universalRel)))
            changed |= AddLocalLibrary(node, $"net.neoforged:neoforge:{forgeVersion}:universal", universalRel);

        var mcpVersion = $"{mcVersion}-{neoFormVersion}";
        var srgRel = $"net/minecraft/client/{mcpVersion}/client-{mcpVersion}-srg.jar";
        var srgPath = ToLocal(librariesDir, srgRel);
        var patchedRel = $"net/neoforged/minecraft-client-patched/{forgeVersion}/minecraft-client-patched-{forgeVersion}.jar";
        var patchedPath = ToLocal(librariesDir, patchedRel);

        if (!File.Exists(srgPath))
        {
            if (!File.Exists(patchedPath))
            {
                log?.Invoke($"NeoForge 缺少 {patchedRel}，无法生成运行时所需的 srg jar。" +
                            "请重新执行一次安装（安装器会重新生成它）。");
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(srgPath)!);
                await Task.Run(() => CreateSrgJar(patchedPath, srgPath), ct).ConfigureAwait(false);
                log?.Invoke($"已由 patched jar 生成 {srgRel}（补 Minecraft-Dists: client）");
            }
        }

        if (File.Exists(srgPath))
            changed |= AddLocalLibrary(node, $"net.minecraft:client:{mcpVersion}:client:srg", srgRel);

        if (!changed) return false;

        var jsonPath = Path.Combine(Paths.VersionsDir(gameDir), versionId, versionId + ".json");
        await File.WriteAllTextAsync(jsonPath, node.ToJsonString(WriteOptions), ct).ConfigureAwait(false);
        log?.Invoke($"已补齐 {versionId} 的 NeoForge 运行依赖（universal / srg）");

        return true;
    }

    private static string GameArgValue(JsonObject node, string flag)
    {
        if (node["arguments"] is not JsonObject arguments) return "";
        if (arguments["game"] is not JsonArray game) return "";

        for (var i = 0; i < game.Count - 1; i++)
        {

            if (game[i] is JsonValue key && key.TryGetValue(out string? token) && token == flag)
                return game[i + 1] is JsonValue value && value.TryGetValue(out string? result) ? result : "";
        }

        return "";
    }

    private static bool AddLocalLibrary(JsonObject node, string name, string relPath)
    {
        var libraries = node["libraries"] as JsonArray ?? new JsonArray();

        foreach (var item in libraries)
        {
            if (item is not JsonObject lib) continue;
            var existing = lib["downloads"]?["artifact"]?["path"]?.GetValue<string>();
            if (string.Equals(existing, relPath, StringComparison.OrdinalIgnoreCase)) return false;
        }

        libraries.Add(new JsonObject
        {
            ["name"] = name,
            ["downloads"] = new JsonObject
            {
                ["artifact"] = new JsonObject
                {
                    ["path"] = relPath,
                    ["url"] = "",
                    ["sha1"] = "",
                    ["size"] = 0
                }
            }
        });

        node["libraries"] = libraries;
        return true;
    }

    private static void CreateSrgJar(string patchedPath, string srgPath)
    {
        var tmp = srgPath + ".part";

        try
        {
            using var source = ZipFile.OpenRead(patchedPath);
            using var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            using var zip = new ZipArchive(output, ZipArchiveMode.Create);

            foreach (var entry in source.Entries)
            {
                var target = zip.CreateEntry(entry.FullName, CompressionLevel.Fastest);
                using var from = entry.Open();
                using var to = target.Open();

                if (entry.FullName.Equals("META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(from, Encoding.UTF8);
                    var text = reader.ReadToEnd();
                    var bytes = new UTF8Encoding(false).GetBytes(WithMinecraftDists(text));
                    to.Write(bytes, 0, bytes.Length);
                }
                else
                {
                    from.CopyTo(to);
                }
            }
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }

        File.Move(tmp, srgPath, true);
    }

    internal static string WithMinecraftDists(string manifest)
    {
        if (manifest.Contains("Minecraft-Dists:", StringComparison.OrdinalIgnoreCase)) return manifest;

        var separator = manifest.Contains("\r\n\r\n", StringComparison.Ordinal) ? "\r\n\r\n" : "\n\n";
        var newline = separator == "\r\n\r\n" ? "\r\n" : "\n";
        var index = manifest.IndexOf(separator, StringComparison.Ordinal);

        var head = index >= 0 ? manifest[..index] : manifest;
        var rest = index >= 0 ? manifest[index..] : "";

        return head + newline + "Minecraft-Dists: client" + rest;
    }

    private static string ToLocal(string librariesDir, string relPath) =>
        Path.Combine(librariesDir, relPath.Replace('/', Path.DirectorySeparatorChar));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch {  }
    }
}

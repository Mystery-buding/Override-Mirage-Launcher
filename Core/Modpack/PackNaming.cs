using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OverrideMirageLauncher.Core;

public static class PackNaming
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string? Validate(string rawName, string gameDir, string? currentId = null)
    {
        var name = rawName.Trim();

        if (name.Length == 0) return "名称不能为空。";
        if (name.Length > 32) return "名称最多 32 个字符。";
        if (name is "." or "..") return "这个名称不可用。";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "名称不能包含 \\ / : * ? \" < > | 等符号。";
        if (name.EndsWith('.') || name.EndsWith(' '))
            return "名称不能以点或空格结尾。";

        if (string.Equals(name, currentId, StringComparison.OrdinalIgnoreCase)) return null;

        var target = Path.Combine(Paths.VersionsDir(gameDir), name);
        if (Directory.Exists(target)) return $"已存在同名整合包：{name}";

        return null;
    }

    public static async Task<string> RenameAsync(string gameDir, string oldId, string newName,
        CancellationToken ct = default)
    {
        var versionsDir = Paths.VersionsDir(gameDir);
        var oldDir = Path.Combine(versionsDir, oldId);
        var newDir = Path.Combine(versionsDir, newName);

        if (!Directory.Exists(oldDir))
            throw new DirectoryNotFoundException($"找不到整合包目录：{oldId}");
        if (Directory.Exists(newDir))
            throw new IOException($"已存在同名整合包：{newName}");

        var oldJson = Path.Combine(oldDir, oldId + ".json");
        if (!File.Exists(oldJson))
            throw new FileNotFoundException($"整合包缺少版本文件：{oldId}.json");

        Directory.Move(oldDir, newDir);

        try
        {

            var sourceJson = Path.Combine(newDir, oldId + ".json");
            var newJson = Path.Combine(newDir, newName + ".json");

            var node = JsonNode.Parse(await File.ReadAllTextAsync(sourceJson, ct).ConfigureAwait(false)) as JsonObject
                       ?? throw new InvalidOperationException($"版本文件格式异常：{oldId}.json");

            node["id"] = newName;
            await File.WriteAllTextAsync(newJson, node.ToJsonString(WriteOptions), ct).ConfigureAwait(false);

            if (!string.Equals(newJson, sourceJson, StringComparison.OrdinalIgnoreCase) && File.Exists(sourceJson))
                File.Delete(sourceJson);

            var oldJar = Path.Combine(newDir, oldId + ".jar");
            if (File.Exists(oldJar))
                File.Move(oldJar, Path.Combine(newDir, newName + ".jar"));

            await UpdateChildInheritsAsync(versionsDir, oldId, newName, ct).ConfigureAwait(false);
        }
        catch
        {

            try
            {
                if (Directory.Exists(newDir) && !Directory.Exists(oldDir))
                    Directory.Move(newDir, oldDir);
            }
            catch
            {

            }

            throw;
        }

        LogBus.Info($"整合包改名：{oldId} → {newName}");
        return newName;
    }

    private static async Task UpdateChildInheritsAsync(string versionsDir, string oldId, string newId,
        CancellationToken ct)
    {
        foreach (var dir in Directory.GetDirectories(versionsDir))
        {
            var id = Path.GetFileName(dir);
            if (string.Equals(id, newId, StringComparison.OrdinalIgnoreCase)) continue;

            var path = Path.Combine(dir, id + ".json");
            if (!File.Exists(path)) continue;

            JsonObject? child;
            try
            {
                child = JsonNode.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)) as JsonObject;
            }
            catch
            {
                continue;
            }

            if (child?["inheritsFrom"]?.GetValue<string>() != oldId) continue;

            child["inheritsFrom"] = newId;
            await File.WriteAllTextAsync(path, child.ToJsonString(WriteOptions), ct).ConfigureAwait(false);
            LogBus.Info($"更新继承引用：{id} 的 inheritsFrom → {newId}");
        }
    }
}

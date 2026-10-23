using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OverrideMirageLauncher.Core;

/// <summary>
/// 合并后的版本：已把 inheritsFrom 链条展开。
/// </summary>
/// <param name="VersionId">请求的版本 id</param>
/// <param name="JarId">继承链最顶层版本 id，客户端 jar 取自该版本</param>
/// <param name="MergedJson">合并后的版本 JSON 文本</param>
public sealed record ResolvedVersion(string VersionId, string JarId, string MergedJson);

/// <summary>
/// 处理模组加载器产生的 inheritsFrom 版本继承：
/// 把父版本的 libraries / arguments / mainClass 等与子版本合并成一份可直接使用的 JSON。
/// </summary>
public static class VersionResolver
{
    private const int MaxDepth = 8;

    public static async Task<ResolvedVersion> ResolveAsync(string gameDir, string versionId,
        CancellationToken ct = default)
    {
        var chain = new List<(string Id, JsonObject Json)>();
        var current = versionId;

        for (var depth = 0; depth < MaxDepth && !string.IsNullOrWhiteSpace(current); depth++)
        {
            var text = await LoadVersionJsonAsync(gameDir, current, ct).ConfigureAwait(false);
            var node = JsonNode.Parse(text) as JsonObject
                       ?? throw new InvalidOperationException($"版本 {current} 的 JSON 不是对象。");

            chain.Add((current, node));
            current = node["inheritsFrom"]?.GetValue<string>() ?? "";
        }

        if (chain.Count == 0)
            throw new InvalidOperationException($"无法解析版本 {versionId}。");

        // 从最顶层父版本开始向下合并
        var merged = (JsonObject)chain[^1].Json.DeepClone();
        for (var i = chain.Count - 2; i >= 0; i--)
            merged = Merge(merged, chain[i].Json);

        var jarId = chain[^1].Id;
        return new ResolvedVersion(versionId, jarId, merged.ToJsonString());
    }

    /// <summary>
    /// 子版本覆盖父版本；libraries 与 arguments 采用追加语义。
    /// </summary>
    private static JsonObject Merge(JsonObject parent, JsonObject child)
    {
        var result = (JsonObject)parent.DeepClone();

        foreach (var pair in child)
        {
            var key = pair.Key;
            var value = pair.Value;

            if (key == "inheritsFrom") continue;

            switch (key)
            {
                case "libraries":
                {
                    var mergedLibs = new JsonArray();
                    var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                    void AddLibrary(JsonNode? node)
                    {
                        if (node is not JsonObject lib) return;
                        var name = lib["name"]?.GetValue<string>() ?? "";
                        var parts = name.Split(':');

                        // 去重键必须带上分类器：org.lwjgl:lwjgl:3.4.3 与
                        // org.lwjgl:lwjgl:3.4.3:natives-windows 是两个不同的产物，
                        // 只用 group:artifact 会让 natives 覆盖主 jar，反之亦然。
                        var identity = parts.Length >= 3
                            ? parts[0] + ":" + parts[1] + ":" + (parts.Length > 3 ? parts[3] : "")
                            : name;

                        if (index.TryGetValue(identity, out var position))
                            mergedLibs[position] = lib.DeepClone();
                        else
                        {
                            index[identity] = mergedLibs.Count;
                            mergedLibs.Add(lib.DeepClone());
                        }
                    }

                    if (result["libraries"] is JsonArray parentLibs)
                        foreach (var lib in parentLibs) AddLibrary(lib);
                    if (value is JsonArray childLibs)
                        foreach (var lib in childLibs) AddLibrary(lib);

                    result[key] = mergedLibs;
                    break;
                }

                case "arguments":
                {
                    var target = result["arguments"] as JsonObject ?? new JsonObject();
                    if (value is JsonObject childArgs)
                    {
                        foreach (var argPair in childArgs)
                        {
                            if (argPair.Value is JsonArray incoming)
                            {
                                var list = target[argPair.Key] as JsonArray ?? new JsonArray();
                                foreach (var item in incoming) list.Add(item?.DeepClone());
                                target[argPair.Key] = list;
                            }
                            else
                            {
                                target[argPair.Key] = argPair.Value?.DeepClone();
                            }
                        }
                    }

                    result[key] = target;
                    break;
                }

                default:
                    result[key] = value?.DeepClone();
                    break;
            }
        }

        return result;
    }

    private static async Task<string> LoadVersionJsonAsync(string gameDir, string id, CancellationToken ct)
    {
        var path = Path.Combine(Paths.VersionsDir(gameDir), id, id + ".json");
        if (File.Exists(path) && new FileInfo(path).Length > 0)
            return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);

        var manifest = await VersionService.GetManifestAsync(ct: ct).ConfigureAwait(false);
        var entry = manifest.Versions.FirstOrDefault(v => v.Id == id)
                    ?? throw new InvalidOperationException($"版本清单里没有 {id}，无法解析继承关系。");

        var text = await Downloader.GetStringAsync(entry.Url, ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text, ct).ConfigureAwait(false);
        return text;
    }
}

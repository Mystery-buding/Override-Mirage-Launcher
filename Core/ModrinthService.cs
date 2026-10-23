using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace OverrideMirageLauncher.Core;

/// <summary>
/// Modrinth 接口封装：搜索 → 取版本 → 下载文件。
/// Modrinth 的开放接口不需要 API Key，只要带上 User-Agent 即可。
/// </summary>
public static class ModrinthService
{
    private const string Api = "https://api.modrinth.com/v2";

    /// <summary>内容类型 → Modrinth 的 project_type。</summary>
    private static string ProjectType(ContentKind kind) => kind switch
    {
        ContentKind.ResourcePack => "resourcepack",
        ContentKind.Shader => "shader",
        _ => "mod"
    };

    /// <summary>
    /// 搜索项目。`mcVersion` / `loader` 为空时不过滤；加载器只对模组有意义
    /// （资源包与光影没有加载器概念）。
    /// </summary>
    public static async Task<List<ContentProject>> SearchAsync(string query, ContentKind kind,
        string? mcVersion, string? loader, int limit = 20, CancellationToken ct = default)
    {
        var facets = new List<List<string>>
        {
            new() { "project_type:" + ProjectType(kind) }
        };

        if (!string.IsNullOrWhiteSpace(mcVersion))
            facets.Add(new List<string> { "versions:" + mcVersion });

        // 模组按加载器过滤；光影按 Iris / OptiFine 之类的分类过滤没有统一写法，跳过
        if (kind == ContentKind.Mod && !string.IsNullOrWhiteSpace(loader))
            facets.Add(new List<string> { "categories:" + loader });

        var facetJson = JsonSerializer.Serialize(facets);
        var text = string.IsNullOrWhiteSpace(query) ? "*" : Uri.EscapeDataString(query.Trim());

        var url = $"{Api}/search?query={text}&limit={limit}&index=relevance" +
                  $"&facets={Uri.EscapeDataString(facetJson)}";

        var json = await GetJsonAsync(url, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var result = new List<ContentProject>();
        if (!doc.RootElement.TryGetProperty("hits", out var hits)) return result;

        foreach (var hit in hits.EnumerateArray())
        {
            result.Add(new ContentProject(
                GetString(hit, "project_id"),
                ContentProvider.Modrinth,
                GetString(hit, "title"),
                GetString(hit, "description"),
                GetString(hit, "author"),
                GetStringOrNull(hit, "icon_url"),
                hit.TryGetProperty("downloads", out var dl) && dl.TryGetInt32(out var n) ? n : 0,
                StringArray(hit, "categories"),
                StringArray(hit, "versions")));
        }

        return result;
    }

    /// <summary>取一个项目可下载的版本列表，按发布时间倒序。</summary>
    public static async Task<List<ContentVersion>> GetVersionsAsync(string projectId,
        string? mcVersion, string? loader, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(mcVersion))
            query.Add("game_versions=" + Uri.EscapeDataString($"[\"{mcVersion}\"]"));
        if (!string.IsNullOrWhiteSpace(loader))
            query.Add("loaders=" + Uri.EscapeDataString($"[\"{loader}\"]"));

        var url = $"{Api}/project/{projectId}/version" + (query.Count > 0 ? "?" + string.Join("&", query) : "");

        var json = await GetJsonAsync(url, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var result = new List<ContentVersion>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var files = new List<ContentFile>();
            if (item.TryGetProperty("files", out var fileArray))
            {
                foreach (var file in fileArray.EnumerateArray())
                {
                    string? sha1 = null;
                    if (file.TryGetProperty("hashes", out var hashes) &&
                        hashes.TryGetProperty("sha1", out var sha1Node))
                        sha1 = sha1Node.GetString();

                    files.Add(new ContentFile(
                        GetString(file, "url"),
                        GetString(file, "filename"),
                        sha1,
                        file.TryGetProperty("size", out var size) && size.TryGetInt64(out var s) ? s : 0,
                        file.TryGetProperty("primary", out var primary) && primary.GetBoolean()));
                }
            }

            var published = DateTimeOffset.MinValue;
            if (item.TryGetProperty("date_published", out var date) &&
                DateTimeOffset.TryParse(date.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var parsed))
                published = parsed;

            result.Add(new ContentVersion(
                GetString(item, "id"),
                ContentProvider.Modrinth,
                GetString(item, "name"),
                GetString(item, "version_number"),
                StringArray(item, "game_versions"),
                StringArray(item, "loaders"),
                GetString(item, "version_type"),
                published,
                files));
        }

        result.Sort((a, b) => b.Published.CompareTo(a.Published));
        return result;
    }

    /// <summary>挑主文件（或唯一文件），下载到目标目录，返回落盘路径。</summary>
    public static async Task<string> DownloadAsync(ContentVersion version, string dir,
        CancellationToken ct = default)
    {
        var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault()
                   ?? throw new InvalidOperationException("这个版本没有可下载的文件。");

        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, file.FileName);

        // Modrinth 的 CDN 不走镜像，rewrite 关掉
        await Downloader.DownloadAsync(file.Url, dest, file.Sha1,
            file.Size > 0 ? file.Size : null, ct, rewrite: false).ConfigureAwait(false);

        return dest;
    }

    /// <summary>
    /// 取项目图标（原始字节），带本地磁盘缓存。
    /// Modrinth 的图标有相当一部分是 webp，WPF 解不了，交给调用方兜底。
    /// </summary>
    public static async Task<byte[]?> GetIconBytesAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var cacheDir = Path.Combine(Paths.BaseDir, "cache", "icons");
        var name = Convert.ToHexString(System.Security.Cryptography.SHA1
            .HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant() + ".img";
        var cachePath = Path.Combine(cacheDir, name);

        if (File.Exists(cachePath))
        {
            try { return await File.ReadAllBytesAsync(cachePath, ct).ConfigureAwait(false); }
            catch { /* 缓存坏了就重新下 */ }
        }

        try
        {
            using var resp = await Downloader.Client
                .GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > 4 * 1024 * 1024) return null;

            Directory.CreateDirectory(cacheDir);
            await File.WriteAllBytesAsync(cachePath, bytes, ct).ConfigureAwait(false);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> GetJsonAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        using var resp = await Downloader.Client
            .GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
    }

    private static string GetString(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string? GetStringOrNull(JsonElement node, string name)
    {
        var text = GetString(node, name);
        return text.Length == 0 ? null : text;
    }

    private static string[] StringArray(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var list = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                list.Add(s);
        }

        return list.ToArray();
    }
}

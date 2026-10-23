using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace OverrideMirageLauncher.Core;

/// <summary>
/// CurseForge 接口封装（api.curseforge.com/v1）。
/// 与 Modrinth 不同，CurseForge 的第三方接口必须带 API Key，
/// Key 在 https://console.curseforge.com 免费申请，填在设置页里。
/// </summary>
public static class CurseForgeService
{
    private const string Api = "https://api.curseforge.com/v1";
    private const int MinecraftGameId = 432;

    // CurseForge 的 classId：6 = 模组，12 = 资源包，6552 = 光影
    private static int ClassId(ContentKind kind) => kind switch
    {
        ContentKind.ResourcePack => 12,
        ContentKind.Shader => 6552,
        _ => 6
    };

    // CurseForge 的 modLoaderType：1 = Forge，4 = Fabric，5 = Quilt，6 = NeoForge
    private static int LoaderTypeId(string? loader) => loader switch
    {
        "forge" => 1,
        "fabric" => 4,
        "quilt" => 5,
        "neoforge" => 6,
        _ => 0
    };

    /// <summary>搜索项目。apiKey 为空会在调用方被拦掉，这里只负责发请求。</summary>
    public static async Task<List<ContentProject>> SearchAsync(string query, ContentKind kind,
        string? mcVersion, string? loader, string apiKey, int limit = 20, CancellationToken ct = default)
    {
        var args = new StringBuilder($"?gameId={MinecraftGameId}&classId={ClassId(kind)}" +
                                     $"&pageSize={limit}&index=0&sortField=2&sortOrder=desc");

        if (!string.IsNullOrWhiteSpace(query))
            args.Append("&searchFilter=").Append(Uri.EscapeDataString(query.Trim()));
        if (!string.IsNullOrWhiteSpace(mcVersion))
            args.Append("&gameVersion=").Append(Uri.EscapeDataString(mcVersion));

        var loaderId = LoaderTypeId(loader);
        if (kind == ContentKind.Mod && loaderId > 0)
            args.Append("&modLoaderType=").Append(loaderId);

        var json = await GetJsonAsync(Api + "/mods/search" + args, apiKey, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var result = new List<ContentProject>();
        if (!doc.RootElement.TryGetProperty("data", out var data)) return result;

        foreach (var item in data.EnumerateArray())
        {
            string? icon = null;
            if (item.TryGetProperty("logo", out var logo) &&
                logo.ValueKind == JsonValueKind.Object &&
                logo.TryGetProperty("thumbnailUrl", out var thumb))
                icon = thumb.GetString();

            var author = "";
            if (item.TryGetProperty("authors", out var authors) &&
                authors.ValueKind == JsonValueKind.Array &&
                authors.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } first)
                author = GetString(first, "name");

            var categories = new List<string>();
            if (item.TryGetProperty("categories", out var cats))
            {
                foreach (var cat in cats.EnumerateArray())
                {
                    if (cat.ValueKind == JsonValueKind.Object && cat.TryGetProperty("slug", out var slug))
                    {
                        if (slug.GetString() is { Length: > 0 } s) categories.Add(s);
                    }
                }
            }

            // 搜索接口不给游戏版本列表，用 latestFilesIndexes 里的去个重
            var versions = new List<string>();
            if (item.TryGetProperty("latestFilesIndexes", out var indexes))
            {
                foreach (var entry in indexes.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object &&
                        entry.TryGetProperty("gameVersion", out var gv) &&
                        gv.GetString() is { Length: > 0 } v && !versions.Contains(v))
                        versions.Add(v);
                }
            }

            result.Add(new ContentProject(
                item.TryGetProperty("id", out var id) && id.TryGetInt32(out var n)
                    ? n.ToString(CultureInfo.InvariantCulture)
                    : "",
                ContentProvider.CurseForge,
                GetString(item, "name"),
                GetString(item, "summary"),
                author,
                icon,
                item.TryGetProperty("downloadCount", out var dl) && dl.TryGetInt32(out var d) ? d : 0,
                categories.ToArray(),
                versions.ToArray()));
        }

        return result;
    }

    /// <summary>取项目的文件列表（按时间倒序），可带游戏版本与加载器过滤。</summary>
    public static async Task<List<ContentVersion>> GetVersionsAsync(string projectId,
        string? mcVersion, string? loader, string apiKey, CancellationToken ct = default)
    {
        var args = new StringBuilder("?pageSize=50&index=0&sortField=1&sortOrder=desc");
        if (!string.IsNullOrWhiteSpace(mcVersion))
            args.Append("&gameVersion=").Append(Uri.EscapeDataString(mcVersion));

        var loaderId = LoaderTypeId(loader);
        if (loaderId > 0) args.Append("&modLoaderType=").Append(loaderId);

        var json = await GetJsonAsync($"{Api}/mods/{projectId}/files" + args, apiKey, ct)
            .ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var result = new List<ContentVersion>();
        if (!doc.RootElement.TryGetProperty("data", out var data)) return result;

        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idNode) && idNode.TryGetInt32(out var n)
                ? n : 0;

            var fileName = GetString(item, "fileName");
            var url = GetString(item, "downloadUrl");

            // 有些老文件没有 downloadUrl，按 CurseForge 的规则用 id 拼 CDN 地址
            if (url.Length == 0 && id > 0 && fileName.Length > 0)
                url = $"https://media.forgecdn.net/files/{id / 1000}/{id % 1000}/{fileName}";

            string? sha1 = null;
            if (item.TryGetProperty("hashes", out var hashes))
            {
                foreach (var hash in hashes.EnumerateArray())
                {
                    if (hash.ValueKind != JsonValueKind.Object) continue;
                    if (hash.TryGetProperty("algo", out var algo) && algo.TryGetInt32(out var a) && a == 1)
                        sha1 = GetString(hash, "value");
                }
            }

            var loaders = new List<string>();
            if (item.TryGetProperty("gameVersions", out var gvs))
            {
                foreach (var gv in gvs.EnumerateArray())
                {
                    if (gv.ValueKind != JsonValueKind.String) continue;
                    if (gv.GetString() is { Length: > 0 } text && LoaderName(text) is { } mapped)
                        loaders.Add(mapped);
                }
            }

            var published = DateTimeOffset.MinValue;
            if (item.TryGetProperty("fileDate", out var date) &&
                DateTimeOffset.TryParse(date.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var parsed))
                published = parsed;

            var versionNumber = GetString(item, "displayName");
            if (versionNumber.Length == 0) versionNumber = fileName;

            var releaseType = item.TryGetProperty("releaseType", out var rt) && rt.TryGetInt32(out var r)
                ? r switch { 1 => "release", 2 => "beta", 3 => "alpha", _ => "release" }
                : "release";

            result.Add(new ContentVersion(
                id.ToString(CultureInfo.InvariantCulture),
                ContentProvider.CurseForge,
                versionNumber,
                versionNumber,
                StringArray(item, "gameVersions"),
                loaders.Distinct().ToArray(),
                releaseType,
                published,
                new[]
                {
                    new ContentFile(url, fileName, sha1,
                        item.TryGetProperty("fileLength", out var size) && size.TryGetInt64(out var s) ? s : 0,
                        true)
                }));
        }

        result.Sort((a, b) => b.Published.CompareTo(a.Published));
        return result;
    }

    /// <summary>下载某个版本的主文件到目标目录。</summary>
    public static async Task<string> DownloadAsync(ContentVersion version, string dir,
        CancellationToken ct = default)
    {
        var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault()
                   ?? throw new InvalidOperationException("这个文件没有下载地址。");

        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, file.FileName);

        await Downloader.DownloadAsync(file.Url, dest, file.Sha1,
            file.Size > 0 ? file.Size : null, ct, rewrite: false).ConfigureAwait(false);

        return dest;
    }

    /// <summary>CurseForge 的 gameVersions 里会混着加载器名，映射成和 Modrinth 一致的写法。</summary>
    private static string? LoaderName(string text) => text.ToLowerInvariant() switch
    {
        "forge" => "forge",
        "fabric" => "fabric",
        "quilt" => "quilt",
        "neoforge" => "neoforge",
        _ => null
    };

    private static async Task<string> GetJsonAsync(string url, string apiKey, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(25));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.Accept.ParseAdd("application/json");

        using var resp = await Downloader.Client
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);

        var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(resp.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "CurseForge 拒绝了请求：API Key 无效或已过期，请到设置页重新填一个。"
                : $"CurseForge 返回 {(int)resp.StatusCode}：{Trim(body)}");
        }

        return body;
    }

    private static string Trim(string text)
    {
        text = text.Replace("\n", " ").Replace("\r", " ");
        return text.Length <= 160 ? text : text[..160] + "…";
    }

    private static string GetString(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

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

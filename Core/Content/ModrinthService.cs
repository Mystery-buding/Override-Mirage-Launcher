using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace OverrideMirageLauncher.Core;

public static class ModrinthService
{
    private const string Api = "https://api.modrinth.com/v2";

    private static string ProjectType(ContentKind kind) => kind switch
    {
        ContentKind.ResourcePack => "resourcepack",
        ContentKind.Shader => "shader",
        _ => "mod"
    };

    public static async Task<List<ContentProject>> SearchAsync(string query, ContentKind kind,
        string? mcVersion, string? loader, int limit = 20, CancellationToken ct = default)
    {
        var facets = new List<List<string>>
        {
            new() { "project_type:" + ProjectType(kind) }
        };

        if (!string.IsNullOrWhiteSpace(mcVersion))
            facets.Add(new List<string> { "versions:" + mcVersion });

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

    public static async Task<string> DownloadAsync(ContentVersion version, string dir,
        CancellationToken ct = default)
    {
        var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault()
                   ?? throw new InvalidOperationException("这个版本没有可下载的文件。");

        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, file.FileName);

        await Downloader.DownloadAsync(file.Url, dest, file.Sha1,
            file.Size > 0 ? file.Size : null, ct, rewrite: false).ConfigureAwait(false);

        return dest;
    }

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
            catch {  }
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

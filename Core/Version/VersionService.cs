using System.Text.Json;
using System.Text.Json.Serialization;

namespace OverrideMirageLauncher.Core;

public sealed class VersionManifest
{
    [JsonPropertyName("latest")] public LatestVersions? Latest { get; set; }
    [JsonPropertyName("versions")] public List<VersionSummary> Versions { get; set; } = new();
}

public sealed class LatestVersions
{
    [JsonPropertyName("release")] public string? Release { get; set; }
    [JsonPropertyName("snapshot")] public string? Snapshot { get; set; }
}

public sealed class VersionSummary
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("releaseTime")] public DateTimeOffset ReleaseTime { get; set; }

    public string Display => Type switch
    {
        "release" => Id,
        "local" => Id,
        "snapshot" => Id,
        "old_beta" => $"{Id}  （Beta）",
        "old_alpha" => $"{Id}  （Alpha）",
        "installed" => $"{Id}  （已安装）",
        _ => $"{Id}  ({Type})"
    };
}

public static class VersionService
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string ManifestMirrorUrl = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";

    private static VersionManifest? _cached;

    public static void ResetCache() => _cached = null;

    public static async Task<VersionManifest> GetManifestAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (_cached is not null && !forceRefresh) return _cached;

        var order = DownloadSource.UseMirror
            ? new[] { ManifestMirrorUrl, ManifestUrl }
            : new[] { ManifestUrl, ManifestMirrorUrl };

        Exception? last = null;
        foreach (var url in order)
        {
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(10));

                var json = await Downloader.GetStringAsync(url, attempt.Token, rewrite: false).ConfigureAwait(false);
                var manifest = JsonSerializer.Deserialize<VersionManifest>(json)
                               ?? throw new InvalidOperationException("版本清单解析失败");

                _cached = manifest;
                return manifest;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                last = new TimeoutException($"10 秒未响应：{url}");
            }
            catch (Exception ex)
            {
                last = ex;
            }

            LogBus.Warn("版本清单源失败：" + last?.Message);
        }

        throw new InvalidOperationException("版本清单获取失败：" + (last?.Message ?? "未知原因"), last);
    }

    public static async Task<(JsonDocument Doc, string JsonText)> GetVersionJsonAsync(
        string versionId, CancellationToken ct = default)
    {
        var manifest = await GetManifestAsync(ct: ct).ConfigureAwait(false);
        var entry = manifest.Versions.FirstOrDefault(v => v.Id == versionId)
                    ?? throw new InvalidOperationException($"版本清单里没有 {versionId}");

        var text = await Downloader.GetStringAsync(entry.Url, ct).ConfigureAwait(false);
        return (JsonDocument.Parse(text), text);
    }
}

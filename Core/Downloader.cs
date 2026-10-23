using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace OverrideMirageLauncher.Core;

public static class Downloader
{
    public static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        })
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OverrideMirageLauncher/1.0");
        return client;
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct = default, bool rewrite = true)
    {
        var target = rewrite ? DownloadSource.Rewrite(url) : url;
        using var resp = await Client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public static async Task DownloadAsync(string url, string destPath, string? sha1, long? size,
        CancellationToken ct = default, bool rewrite = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        if (File.Exists(destPath))
        {
            if (size is { } s && new FileInfo(destPath).Length == s) return;
            if (size is null && sha1 is null) return;
        }

        var target = rewrite ? DownloadSource.Rewrite(url) : url;
        var tmp = destPath + ".part";
        try
        {
            using (var resp = await Client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            if (sha1 is not null)
            {
                var actual = await ComputeSha1Async(tmp, ct).ConfigureAwait(false);
                if (!string.Equals(actual, sha1, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tmp);
                    throw new InvalidDataException($"文件校验失败：{Path.GetFileName(destPath)}");
                }
            }

            File.Move(tmp, destPath, true);
        }
        finally
        {
            if (File.Exists(tmp)) TryDelete(tmp);
        }
    }

    public static async Task<string> ComputeSha1Async(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hash = await SHA1.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static async Task RunAllAsync(IReadOnlyList<Func<Task>> jobs, int maxDegree,
        Action? onOneDone = null, CancellationToken ct = default)
    {
        if (jobs.Count == 0) return;

        using var semaphore = new SemaphoreSlim(Math.Max(1, maxDegree));
        var tasks = new Task[jobs.Count];

        for (var i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            tasks[i] = Task.Run(async () =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await job().ConfigureAwait(false);
                }
                finally
                {
                    semaphore.Release();
                    onOneDone?.Invoke();
                }
            }, ct);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch {  }
    }
}

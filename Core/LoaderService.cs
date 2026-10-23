using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

namespace OverrideMirageLauncher.Core;

public enum LoaderKind
{
    Fabric,
    Quilt,
    Forge,
    NeoForge
}

public static class LoaderService
{
    public static string DisplayName(LoaderKind kind) => kind switch
    {
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        _ => kind.ToString()
    };

    private const string FabricOfficialMeta = "https://meta.fabricmc.net/v2/versions/loader";
    private const string FabricMirrorMeta = "https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader";
    private const string QuiltMeta = "https://meta.quiltmc.org/v3/versions/loader";
    private const string ForgeOfficialMaven = "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";
    private const string ForgeMirrorMaven = "https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/maven-metadata.xml";
    private const string NeoForgeOfficialMaven = "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";
    private const string NeoForgeMirrorMaven = "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/maven-metadata.xml";

    public static async Task<IReadOnlyList<string>> GetLoaderVersionsAsync(
        LoaderKind kind, string mcVersion, CancellationToken ct = default, bool preferMirror = false)
    {
        using var outer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        outer.CancelAfter(TimeSpan.FromSeconds(45));
        var token = outer.Token;

        try
        {
            switch (kind)
            {
                case LoaderKind.Fabric:
                    return await GetFabricVersionsAsync(
                        $"{FabricOfficialMeta}/{mcVersion}", $"{FabricMirrorMeta}/{mcVersion}", token, preferMirror);

                case LoaderKind.Quilt:
                    return await GetFabricVersionsAsync(
                        $"{QuiltMeta}/{mcVersion}", $"{QuiltMeta}/{mcVersion}", token, preferMirror);

                case LoaderKind.Forge:
                    return await GetForgeVersionsAsync(mcVersion, token, preferMirror);

                case LoaderKind.NeoForge:
                    return await GetNeoForgeVersionsAsync(mcVersion, token, preferMirror);

                default:
                    return Array.Empty<string>();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"查询 {DisplayName(kind)} 版本超时，官方源与镜像均未在 45 秒内返回。");
        }
    }

    private static IEnumerable<string> SourceOrder(string official, string mirror, bool preferMirror = false) =>
        preferMirror || DownloadSource.UseMirror
            ? new[] { mirror, official }
            : new[] { official, mirror };

    private static async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            return await Downloader.GetStringAsync(url, attempt.Token, rewrite: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"10 秒未响应：{url}");
        }
    }

    private static async Task<IReadOnlyList<string>> GetFabricVersionsAsync(
        string officialUrl, string mirrorUrl, CancellationToken ct, bool preferMirror = false)
    {
        Exception? last = null;

        foreach (var url in SourceOrder(officialUrl, mirrorUrl, preferMirror).Distinct())
        {
            try
            {
                var json = await FetchAsync(url, ct).ConfigureAwait(false);
                var versions = ParseFabricLoaderVersions(json);
                if (versions.Count > 0)
                {
                    LogBus.Info($"Fabric 版本来源：{url}（{versions.Count} 个）");
                    return versions;
                }

                last = new InvalidOperationException($"{url} 返回 0 个版本");
            }
            catch (Exception ex)
            {
                last = ex;
                LogBus.Warn("Fabric 版本源失败：" + ex.Message);
            }
        }

        throw new InvalidOperationException("Fabric 版本查询失败：" + (last?.Message ?? "未知原因"));
    }

    private static List<string> ParseFabricLoaderVersions(string json)
    {
        using var doc = JsonDocument.Parse(json);

        var result = new List<string>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("loader", out var loader) &&
                loader.TryGetProperty("version", out var version) &&
                version.GetString() is { Length: > 0 } text)
                result.Add(text);
        }

        return result.Take(40).ToList();
    }

    private static async Task<IReadOnlyList<string>> GetForgeVersionsAsync(string mcVersion, CancellationToken ct,
        bool preferMirror = false)
    {
        Exception? last = null;

        foreach (var url in SourceOrder(ForgeOfficialMaven, ForgeMirrorMaven, preferMirror))
        {
            try
            {
                var xml = await FetchAsync(url, ct).ConfigureAwait(false);
                var versions = ParseForgeVersions(xml, mcVersion);
                if (versions.Count > 0)
                {
                    LogBus.Info($"Forge 版本来源：{url}（{versions.Count} 个）");
                    return versions;
                }

                last = new InvalidOperationException($"{url} 没有 {mcVersion} 的条目");
            }
            catch (Exception ex)
            {
                last = ex;
                LogBus.Warn("Forge 版本源失败：" + ex.Message);
            }
        }

        try
        {
            var json = await FetchAsync(
                "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", ct)
                .ConfigureAwait(false);
            var versions = ParseForgePromotions(json, mcVersion);
            if (versions.Count > 0) return versions;
        }
        catch (Exception ex)
        {
            last = ex;
        }

        throw new InvalidOperationException("Forge 版本查询失败：" + (last?.Message ?? "未知原因"));
    }

    private static List<string> ParseForgeVersions(string xml, string mcVersion) =>
        XDocument.Parse(xml)
            .Descendants("version")
            .Select(e => e.Value.Split('-'))
            .Where(parts => parts.Length >= 2 && parts[0] == mcVersion)
            .Select(parts => parts[1])
            .Where(v => v.Length > 0)
            .Distinct()
            .Reverse()
            .Take(40)
            .ToList();

    private static List<string> ParseForgePromotions(string json, string mcVersion)
    {
        using var doc = JsonDocument.Parse(json);

        var result = new List<string>();
        if (!doc.RootElement.TryGetProperty("promos", out var promos)) return result;

        foreach (var suffix in new[] { "recommended", "latest" })
        {
            var key = $"{mcVersion}-{suffix}";
            if (promos.TryGetProperty(key, out var value) && value.GetString() is { Length: > 0 } ver &&
                !result.Contains(ver))
                result.Add(ver);
        }

        return result;
    }

    private static async Task<IReadOnlyList<string>> GetNeoForgeVersionsAsync(string mcVersion, CancellationToken ct,
        bool preferMirror = false)
    {
        var prefix = NeoForgePrefix(mcVersion);
        if (prefix.Length == 0) return Array.Empty<string>();

        Exception? last = null;

        foreach (var url in SourceOrder(NeoForgeOfficialMaven, NeoForgeMirrorMaven, preferMirror))
        {
            try
            {
                var xml = await FetchAsync(url, ct).ConfigureAwait(false);
                var versions = ParseNeoForgeVersions(xml, prefix);
                if (versions.Count > 0)
                {
                    LogBus.Info($"NeoForge 版本来源：{url}（{versions.Count} 个）");
                    return versions;
                }

                last = new InvalidOperationException($"{url} 没有 {prefix}* 版本");
            }
            catch (Exception ex)
            {
                last = ex;
                LogBus.Warn("NeoForge 版本源失败：" + ex.Message);
            }
        }

        throw new InvalidOperationException("NeoForge 版本查询失败：" + (last?.Message ?? "未知原因"));
    }

    private static List<string> ParseNeoForgeVersions(string xml, string prefix) =>
        XDocument.Parse(xml)
            .Descendants("version")
            .Select(e => e.Value)
            .Where(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Reverse()
            .Take(25)
            .ToList();

    private static IEnumerable<string> MavenCandidates(string url, bool preferMirror = false)
    {
        const string forgeHost = "https://maven.minecraftforge.net";
        const string neoHost = "https://maven.neoforged.net/releases";
        const string mirrorBase = "https://bmclapi2.bangbang93.com/maven";

        string? mirror = null;
        if (url.StartsWith(neoHost, StringComparison.OrdinalIgnoreCase))
            mirror = mirrorBase + url[neoHost.Length..];
        else if (url.StartsWith(forgeHost, StringComparison.OrdinalIgnoreCase))
            mirror = mirrorBase + url[forgeHost.Length..];

        if (mirror is null)
        {
            yield return url;
            yield break;
        }

        if (preferMirror || DownloadSource.UseMirror)
        {
            yield return mirror;
            yield return url;
        }
        else
        {
            yield return url;
            yield return mirror;
        }
    }

    private static string NeoForgePrefix(string mcVersion)
    {
        var rest = mcVersion.StartsWith("1.", StringComparison.Ordinal) ? mcVersion[2..] : mcVersion;
        if (rest.Length == 0) return "";
        return rest.Contains('.') ? rest + "." : rest + ".0.";
    }

    public static async Task<string> InstallAsync(AppConfig cfg, LoaderKind kind, string mcVersion,
        string loaderVersion, IProgress<InstallProgress>? progress, Action<string> log,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mcVersion) || string.IsNullOrWhiteSpace(loaderVersion))
            throw new ArgumentException("原版版本号与加载器版本号都不能为空。");

        log($"准备为 {mcVersion} 安装 {DisplayName(kind)} {loaderVersion}");

        progress?.Report(new InstallProgress($"检查原版 {mcVersion}…", 1));
        await MinecraftInstaller.InstallAsync(cfg, mcVersion, progress, ct).ConfigureAwait(false);

        return kind switch
        {
            LoaderKind.Fabric => await InstallMetaProfileAsync(cfg,
                $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}/{loaderVersion}/profile/json",
                progress, log, ct).ConfigureAwait(false),

            LoaderKind.Quilt => await InstallMetaProfileAsync(cfg,
                $"https://meta.quiltmc.org/v3/versions/loader/{mcVersion}/{loaderVersion}/profile/json",
                progress, log, ct).ConfigureAwait(false),

            LoaderKind.Forge => await InstallWithInstallerAsync(cfg, kind, mcVersion, loaderVersion, progress, log, ct)
                .ConfigureAwait(false),

            LoaderKind.NeoForge => await InstallWithInstallerAsync(cfg, kind, mcVersion, loaderVersion, progress, log, ct)
                .ConfigureAwait(false),

            _ => throw new NotSupportedException($"不支持的加载器：{kind}")
        };
    }

    private static async Task<string> InstallMetaProfileAsync(AppConfig cfg, string profileUrl,
        IProgress<InstallProgress>? progress, Action<string> log, CancellationToken ct)
    {
        progress?.Report(new InstallProgress("获取加载器配置…", 35));
        var profileJson = await Downloader.GetStringAsync(profileUrl, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(profileJson);
        var id = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("加载器 meta 未返回版本 id。");

        var dir = Path.Combine(Paths.VersionsDir(cfg.GameDir), id);
        Directory.CreateDirectory(dir);
        var jsonPath = Path.Combine(dir, id + ".json");
        await File.WriteAllTextAsync(jsonPath, profileJson, ct).ConfigureAwait(false);

        log($"已写入加载器版本 {id}");
        progress?.Report(new InstallProgress($"安装 {id} 依赖…", 40));
        await MinecraftInstaller.InstallAsync(cfg, id, progress, ct).ConfigureAwait(false);

        return id;
    }

    private static async Task<string> InstallWithInstallerAsync(AppConfig cfg, LoaderKind kind, string mcVersion,
        string loaderVersion, IProgress<InstallProgress>? progress, Action<string> log, CancellationToken ct)
    {

        using var mirrorScope = DownloadSource.PreferMirror();

        string installerUrl;
        string versionId;

        if (kind == LoaderKind.Forge)
        {
            installerUrl = "https://maven.minecraftforge.net/net/minecraftforge/forge/" +
                           $"{mcVersion}-{loaderVersion}/forge-{mcVersion}-{loaderVersion}-installer.jar";
            versionId = $"{mcVersion}-forge-{loaderVersion}";
        }
        else
        {
            installerUrl = "https://maven.neoforged.net/releases/net/neoforged/neoforge/" +
                           $"{loaderVersion}/neoforge-{loaderVersion}-installer.jar";
            versionId = $"neoforge-{loaderVersion}";
        }

        var installersDir = Path.Combine(Paths.BaseDir, "installers");
        Directory.CreateDirectory(installersDir);
        var installerPath = Path.Combine(installersDir, Path.GetFileName(new Uri(installerUrl).AbsolutePath));

        progress?.Report(new InstallProgress("下载安装器…", 35));

        Exception? lastError = null;
        foreach (var candidate in MavenCandidates(installerUrl))
        {
            try
            {
                log($"下载安装器：{candidate}");
                await Downloader.DownloadAsync(candidate, installerPath, null, null, ct, rewrite: false)
                    .ConfigureAwait(false);
                lastError = null;
                break;
            }
            catch (Exception ex)
            {
                lastError = ex;
                log("安装器下载失败：" + ex.Message);
            }
        }

        if (lastError is not null)
            throw new InvalidOperationException("安装器下载失败：" + lastError.Message, lastError);

        EnsureLauncherProfiles(cfg.GameDir, log);

        progress?.Report(new InstallProgress("预置安装器依赖…", 42));
        await PrefetchInstallerLibrariesAsync(installerPath, cfg.GameDir, log, ct).ConfigureAwait(false);

        progress?.Report(new InstallProgress("运行安装器（可能需要几分钟）…", 45));
        await RunInstallerAsync(installerPath, cfg.GameDir, log, ct).ConfigureAwait(false);

        var jsonPath = Path.Combine(Paths.VersionsDir(cfg.GameDir), versionId, versionId + ".json");
        if (!File.Exists(jsonPath))
            throw new InvalidOperationException(
                $"安装器已执行，但没有找到 {versionId} 的版本文件。可能该组合的版本号不匹配。");

        log($"安装器完成，生成版本 {versionId}");

        if (kind == LoaderKind.NeoForge)
        {
            var text = await File.ReadAllTextAsync(jsonPath, ct).ConfigureAwait(false);
            await NeoForgeRuntime.EnsureAsync(cfg.GameDir, versionId, text, log, ct).ConfigureAwait(false);
        }

        progress?.Report(new InstallProgress($"补全 {versionId} 依赖…", 75));
        await MinecraftInstaller.InstallAsync(cfg, versionId, progress, ct).ConfigureAwait(false);

        return versionId;
    }

    private static void EnsureLauncherProfiles(string gameDir, Action<string> log)
    {
        try
        {
            Directory.CreateDirectory(gameDir);
            var path = Path.Combine(gameDir, "launcher_profiles.json");
            if (File.Exists(path) && new FileInfo(path).Length > 2) return;

            File.WriteAllText(path, """
            {
              "profiles": {},
              "settings": { "enableSnapshots": false },
              "version": 3
            }
            """);

            log("已生成 launcher_profiles.json（安装器运行前提）");
        }
        catch (Exception ex)
        {
            log("生成 launcher_profiles.json 失败：" + ex.Message);
        }
    }

    private static async Task PrefetchInstallerLibrariesAsync(string installerJar, string gameDir,
        Action<string> log, CancellationToken ct)
    {
        var artifacts = new Dictionary<string, (string Path, string Url, string? Sha1, long Size)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var zip = ZipFile.OpenRead(installerJar);

            foreach (var name in new[] { "version.json", "install_profile.json" })
            {
                var entry = zip.GetEntry(name);
                if (entry is null) continue;

                using var stream = entry.Open();
                using var doc = JsonDocument.Parse(stream);

                if (!doc.RootElement.TryGetProperty("libraries", out var libs) ||
                    libs.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var lib in libs.EnumerateArray())
                {
                    if (!lib.TryGetProperty("downloads", out var downloads) ||
                        !downloads.TryGetProperty("artifact", out var artifact))
                        continue;

                    var path = artifact.TryGetProperty("path", out var p) ? p.GetString() : null;
                    var url = artifact.TryGetProperty("url", out var u) ? u.GetString() : null;
                    if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(url)) continue;

                    var sha1 = artifact.TryGetProperty("sha1", out var sha) ? sha.GetString() : null;
                    var size = artifact.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var v) ? v : 0;
                    artifacts[path] = (path, url, sha1, size);
                }
            }
        }
        catch (Exception ex)
        {
            log("读取安装器依赖清单失败（跳过预置）：" + ex.Message);
            return;
        }

        if (artifacts.Count == 0) return;

        var librariesDir = Paths.LibrariesDir(gameDir);
        var jobs = new List<Func<Task>>();

        foreach (var (_, item) in artifacts)
        {
            var dest = Path.Combine(librariesDir, item.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(dest) && (item.Size <= 0 || new FileInfo(dest).Length == item.Size)) continue;

            jobs.Add(async () =>
            {
                Exception? last = null;
                foreach (var candidate in MavenCandidates(item.Url, preferMirror: true))
                {
                    try
                    {
                        await Downloader.DownloadAsync(candidate, dest, item.Sha1,
                            item.Size > 0 ? item.Size : null, ct, rewrite: false).ConfigureAwait(false);
                        last = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                    }
                }

                if (last is not null)
                    throw new InvalidOperationException($"依赖下载失败 {item.Path}：{last.Message}", last);
            });
        }

        if (jobs.Count == 0)
        {
            log("安装器依赖已齐全，跳过预下载");
            return;
        }

        log($"预置安装器依赖：{jobs.Count} 个文件（镜像优先）");
        await Downloader.RunAllAsync(jobs, 8, ct: ct).ConfigureAwait(false);
        log("安装器依赖预置完成");
    }

    private static async Task RunInstallerAsync(string installerJar, string gameDir,        Action<string> log, CancellationToken ct)
    {
        var javas = JavaFinder.Discover(gameDir);
        var java = javas.OrderByDescending(j => j.MajorVersion).FirstOrDefault()
                   ?? throw new InvalidOperationException("没有找到可用的 Java，无法运行加载器安装器。");

        var javaExe = java.Path;
        var dir = Path.GetDirectoryName(javaExe);
        if (dir is not null)
        {
            var consoleJava = Path.Combine(dir, "java.exe");
            if (File.Exists(consoleJava)) javaExe = consoleJava;
        }

        var psi = new ProcessStartInfo(javaExe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(installerJar)!
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installerJar);
        psi.ArgumentList.Add("--installClient");
        psi.ArgumentList.Add(gameDir);

        log($"执行：{Path.GetFileName(javaExe)} -jar {Path.GetFileName(installerJar)} --installClient \"{gameDir}\"");

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log("[安装器] " + e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log("[安装器] " + e.Data); };

        if (!process.Start())
            throw new InvalidOperationException("加载器安装器启动失败。");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var finished = await Task.Run(() => process.WaitForExit(900_000), ct).ConfigureAwait(false);
        if (!finished)
        {
            try { process.Kill(true); } catch {  }
            throw new TimeoutException("加载器安装器运行超时（15 分钟）。");
        }

        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"加载器安装器退出码 {process.ExitCode}，请查看日志。");
    }
}

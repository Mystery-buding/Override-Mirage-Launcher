using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace OverrideMirageLauncher.Core;

public readonly record struct InstallProgress(string Stage, double Percent);

public sealed record LibraryFile(
    string Name,
    string RelativePath,
    string Url,
    string? Sha1,
    long Size,
    bool IsNative);

/// <summary>
/// 负责把某个版本完整装到本地：客户端 jar、依赖库、natives、资源文件、日志配置。
/// 支持 inheritsFrom（模组加载器版本会继承原版）。
/// </summary>
public static class MinecraftInstaller
{
    public static async Task InstallAsync(AppConfig cfg, string versionId,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        var gameDir = cfg.GameDir;
        var versionDir = Path.Combine(Paths.VersionsDir(gameDir), versionId);
        Directory.CreateDirectory(versionDir);

        void Report(string stage, double percent) => progress?.Report(new InstallProgress(stage, percent));

        // ---------- 1. 解析版本（展开 inheritsFrom） ----------
        Report("解析版本信息…", 1);
        var resolved = await VersionResolver.ResolveAsync(gameDir, versionId, ct).ConfigureAwait(false);

        if (resolved.JarId != versionId)
            LogBus.Info($"版本 {versionId} 继承自 {resolved.JarId}，已合并");

        using var doc = JsonDocument.Parse(resolved.MergedJson);
        var root = doc.RootElement;

        Report("版本信息已就绪", 5);

        // ---------- 2. 客户端 jar（取自继承链最顶层） ----------
        var jarDir = Path.Combine(Paths.VersionsDir(gameDir), resolved.JarId);
        var jarPath = Path.Combine(jarDir, resolved.JarId + ".jar");
        if (root.TryGetProperty("downloads", out var downloads) &&
            downloads.TryGetProperty("client", out var client) &&
            client.ValueKind == JsonValueKind.Object)
        {
            Report("下载客户端…", 6);
            Directory.CreateDirectory(jarDir);
            await Downloader.DownloadAsync(
                client.GetProperty("url").GetString()!,
                jarPath,
                client.TryGetProperty("sha1", out var s1) ? s1.GetString() : null,
                client.TryGetProperty("size", out var sz) ? sz.GetInt64() : null,
                ct).ConfigureAwait(false);
            LogBus.Info($"客户端 jar 就绪：{jarPath}");
        }
        else
        {
            LogBus.Warn("该版本没有 client 下载项，跳过客户端 jar。");
        }

        Report("客户端就绪", 30);

        // ---------- 3. 依赖库 ----------
        var libraries = CollectLibraries(root);
        var librariesDir = Paths.LibrariesDir(gameDir);
        var nativeJars = new List<string>();
        var downloadJobs = new List<Func<Task>>();

        foreach (var lib in libraries)
        {
            var dest = Path.Combine(librariesDir, lib.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (lib.IsNative) nativeJars.Add(dest);

            // 无下载地址的是本地生成的构件，已经由安装器放好，只参与 classpath
            if (lib.Url.Length == 0) continue;

            var expectedSize = lib.Size > 0 ? lib.Size : (long?)null;
            downloadJobs.Add(() => Downloader.DownloadAsync(lib.Url, dest, lib.Sha1, expectedSize, ct));
        }

        var done = 0;
        var total = Math.Max(1, downloadJobs.Count);

        Report($"下载依赖库（0/{downloadJobs.Count}）…", 30);
        await Downloader.RunAllAsync(downloadJobs, 8, () =>
        {
            var current = Interlocked.Increment(ref done);
            Report($"下载依赖库（{current}/{total}）…", 30 + 35.0 * current / total);
        }, ct).ConfigureAwait(false);

        LogBus.Info($"依赖库就绪：{libraries.Count} 个（需下载 {downloadJobs.Count} 个）");

        // ---------- 4. 解压 natives ----------
        Report("解压本地库…", 66);
        var nativeDir = Path.Combine(versionDir, "natives");
        if (Directory.Exists(nativeDir))
        {
            try { Directory.Delete(nativeDir, true); } catch { /* 文件被占用时忽略 */ }
        }
        Directory.CreateDirectory(nativeDir);
        ExtractNatives(nativeJars, nativeDir);
        Report("本地库就绪", 70);

        // ---------- 5. 资源文件 ----------
        if (root.TryGetProperty("assetIndex", out var assetIndex))
        {
            var assetIndexId = VersionJson.GetString(assetIndex, "id");
            var indexPath = Path.Combine(Paths.IndexesDir(gameDir), assetIndexId + ".json");

            await Downloader.DownloadAsync(
                VersionJson.GetString(assetIndex, "url"),
                indexPath,
                assetIndex.TryGetProperty("sha1", out var ais) ? ais.GetString() : null,
                assetIndex.TryGetProperty("size", out var aiz) ? aiz.GetInt64() : null,
                ct).ConfigureAwait(false);

            var indexText = await File.ReadAllTextAsync(indexPath, ct).ConfigureAwait(false);
            using var indexDoc = JsonDocument.Parse(indexText);

            if (indexDoc.RootElement.TryGetProperty("virtual", out var virt) && virt.ValueKind == JsonValueKind.True)
                LogBus.Warn("该版本使用 virtual 资源布局（1.6 及更早），本启动器未适配，可能缺少音效等资源。");

            var objectsDir = Paths.ObjectsDir(gameDir);
            var assetJobs = new List<Func<Task>>();

            if (indexDoc.RootElement.TryGetProperty("objects", out var objects) &&
                objects.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in objects.EnumerateObject())
                {
                    var hash = VersionJson.GetString(prop.Value, "hash");
                    var size = prop.Value.TryGetProperty("size", out var esz) ? esz.GetInt64() : 0;
                    if (hash.Length < 2) continue;

                    var dest = Path.Combine(objectsDir, hash[..2], hash);
                    assetJobs.Add(() => Downloader.DownloadAsync(
                        $"https://resources.download.minecraft.net/{hash[..2]}/{hash}",
                        dest, hash, size, ct));
                }
            }

            var assetTotal = Math.Max(1, assetJobs.Count);
            var assetDone = 0;
            Report($"下载资源文件（0/{assetJobs.Count}）…", 70);

            await Downloader.RunAllAsync(assetJobs, 16, () =>
            {
                var current = Interlocked.Increment(ref assetDone);
                if (current % 25 == 0 || current == assetTotal)
                    Report($"下载资源文件（{current}/{assetTotal}）…", 70 + 28.0 * current / assetTotal);
            }, ct).ConfigureAwait(false);

            LogBus.Info($"资源文件就绪：{assetJobs.Count} 个（索引 {assetIndexId}）");
        }

        // ---------- 6. 日志配置 ----------
        if (root.TryGetProperty("logging", out var logging) &&
            logging.TryGetProperty("client", out var clientLog) &&
            clientLog.TryGetProperty("file", out var logFile))
        {
            var id = VersionJson.GetString(logFile, "id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                var dest = Path.Combine(Paths.LogConfigsDir(gameDir), id);
                await Downloader.DownloadAsync(
                    VersionJson.GetString(logFile, "url"), dest,
                    logFile.TryGetProperty("sha1", out var ls) ? ls.GetString() : null,
                    logFile.TryGetProperty("size", out var lz) ? lz.GetInt64() : null,
                    ct).ConfigureAwait(false);
            }
        }

        Report("安装完成", 100);
        LogBus.Info($"版本 {versionId} 安装完成");
    }

    /// <summary>
    /// 按当前平台筛选出需要的依赖库文件。
    /// 同时支持两种清单格式：Mojang 的 downloads.artifact，以及 Fabric/Quilt 的 Maven 坐标 + 仓库地址。
    /// </summary>
    public static List<LibraryFile> CollectLibraries(JsonElement root)
    {
        var result = new List<LibraryFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!root.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var lib in libraries.EnumerateArray())
        {
            if (!VersionJson.RulesAllow(lib)) continue;

            var name = VersionJson.GetString(lib, "name");
            if (name.Length == 0) continue;

            // 新版清单里 natives-windows 与 natives-windows-arm64 的 rules 完全一样，
            // 只能靠库名里的分类器判断架构，否则会把 ARM64 的 dll 解到 x64 机器上。
            var nameParts = name.Split(':');
            var nameClassifier = nameParts.Length >= 4 ? nameParts[3] : null;
            if (!ArchMatches(nameClassifier)) continue;

            var isNativeLib = name.Contains(":natives-", StringComparison.OrdinalIgnoreCase);

            // 格式一：downloads.artifact
            var hasDownloads = lib.TryGetProperty("downloads", out var downloads) &&
                               downloads.ValueKind == JsonValueKind.Object;

            if (hasDownloads &&
                downloads.TryGetProperty("artifact", out var artifact) &&
                artifact.ValueKind == JsonValueKind.Object)
            {
                var rel = VersionJson.GetString(artifact, "path");
                var url = VersionJson.GetString(artifact, "url");

                // url 为空表示这是安装器在本地生成的构件（如 Forge 补丁客户端 jar）。
                // 它不需要下载，但必须进 classpath，否则 Forge 找不到 .forge_patched_minecraft。
                if (rel.Length > 0 && seen.Add(rel))
                {
                    result.Add(new LibraryFile(
                        name, rel, url,
                        artifact.TryGetProperty("sha1", out var s) ? s.GetString() : null,
                        artifact.TryGetProperty("size", out var z) ? z.GetInt64() : 0,
                        isNativeLib));
                }
            }
            else
            {
                // 格式二：Maven 坐标 + 仓库基地址
                var repo = VersionJson.GetString(lib, "url");
                var rel = MavenRelativePath(name);
                if (repo.Length > 0 && rel is not null && seen.Add(rel))
                {
                    var sha1 = VersionJson.GetString(lib, "sha1");
                    result.Add(new LibraryFile(
                        name, rel, repo.TrimEnd('/') + "/" + rel,
                        sha1.Length > 0 ? sha1 : null,
                        0,
                        isNativeLib));
                }
            }

            // 老格式：natives 分类器单独下载
            if (hasDownloads && lib.TryGetProperty("natives", out var natives) &&
                natives.ValueKind == JsonValueKind.Object &&
                natives.TryGetProperty("windows", out var win))
            {
                var classifier = win.GetString();
                if (string.IsNullOrWhiteSpace(classifier)) continue;
                classifier = classifier.Replace("${arch}", Environment.Is64BitProcess ? "64" : "32");
                if (!ArchMatches(classifier)) continue;

                if (downloads.TryGetProperty("classifiers", out var classifiers) &&
                    classifiers.TryGetProperty(classifier, out var cf) &&
                    cf.ValueKind == JsonValueKind.Object)
                {
                    var rel = VersionJson.GetString(cf, "path");
                    var url = VersionJson.GetString(cf, "url");
                    if (rel.Length > 0 && url.Length > 0 && seen.Add(rel))
                    {
                        result.Add(new LibraryFile(
                            name, rel, url,
                            cf.TryGetProperty("sha1", out var s) ? s.GetString() : null,
                            cf.TryGetProperty("size", out var z) ? z.GetInt64() : 0,
                            true));
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 把 group:artifact:version[:classifier] 转成 Maven 仓库内的相对路径。
    /// </summary>
    private static string? MavenRelativePath(string name)
    {
        var parts = name.Split(':');
        if (parts.Length < 3) return null;

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? parts[3] : null;

        var file = artifact + "-" + version + (classifier is null ? "" : "-" + classifier) + ".jar";
        return $"{group}/{artifact}/{version}/{file}";
    }

    /// <summary>
    /// 判断分类器是否适用于当前进程架构（本项目只构建 x64）。
    /// </summary>
    private static bool ArchMatches(string? classifier)
    {
        if (string.IsNullOrWhiteSpace(classifier)) return true;

        var isArm64 = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                      == System.Runtime.InteropServices.Architecture.Arm64;

        if (classifier.Contains("arm64", StringComparison.OrdinalIgnoreCase)) return isArm64;
        if (classifier.Contains("x86", StringComparison.OrdinalIgnoreCase)) return !isArm64 && !Environment.Is64BitProcess;
        if (classifier.Contains("x64", StringComparison.OrdinalIgnoreCase)) return !isArm64 && Environment.Is64BitProcess;

        return true;
    }

    private static void ExtractNatives(IEnumerable<string> jars, string nativeDir)
    {
        foreach (var jar in jars)
        {
            if (!File.Exists(jar)) continue;
            try
            {
                using var zip = ZipFile.OpenRead(jar);
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;

                    var fileName = Path.GetFileName(entry.FullName);
                    if (fileName.Length == 0) continue;

                    var ext = Path.GetExtension(fileName);
                    if (!ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".so", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".dylib", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".jnilib", StringComparison.OrdinalIgnoreCase))
                        continue;

                    entry.ExtractToFile(Path.Combine(nativeDir, fileName), true);
                }
            }
            catch (Exception ex)
            {
                LogBus.Warn($"解压 {Path.GetFileName(jar)} 失败：{ex.Message}");
            }
        }
    }
}

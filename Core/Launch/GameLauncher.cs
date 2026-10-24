using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OverrideMirageLauncher.Core;

public static class GameLauncher
{
    public static Process Launch(AppConfig cfg, ResolvedVersion resolved, JavaInfo java, Action<string> log,
        AuthSession? auth = null)
    {
        var session = auth ?? cfg.CreateSession();
        var gameDir = cfg.GameDir;
        var versionId = resolved.VersionId;
        var versionDir = Path.Combine(Paths.VersionsDir(gameDir), versionId);

        var runDir = cfg.RunDir(versionId);
        Directory.CreateDirectory(runDir);
        MigrateLegacyFiles(gameDir, runDir, log);
        EnsureRunFolders(runDir, log);

        using var doc = System.Text.Json.JsonDocument.Parse(resolved.MergedJson);
        var root = doc.RootElement;

        var librariesDir = Paths.LibrariesDir(gameDir);
        var classpathParts = new List<string>();
        foreach (var lib in MinecraftInstaller.CollectLibraries(root))
        {
            if (lib.IsNative) continue;
            classpathParts.Add(Path.Combine(librariesDir,
                lib.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        var jarPath = Path.Combine(Paths.VersionsDir(gameDir), resolved.JarId, resolved.JarId + ".jar");
        if (!File.Exists(jarPath))
            throw new FileNotFoundException($"缺少客户端 jar，请先安装版本：{jarPath}");

        if (File.Exists(jarPath)) classpathParts.Add(jarPath);

        var classpath = string.Join(';', classpathParts);
        var nativesDir = Path.Combine(versionDir, "natives");

        var assetIndexId = root.TryGetProperty("assetIndex", out var ai)
            ? VersionJson.GetString(ai, "id")
            : "legacy";

        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth_player_name"] = session.PlayerName,
            ["version_name"] = versionId,
            ["game_directory"] = runDir,
            ["assets_root"] = Paths.AssetsDir(gameDir),
            ["game_assets"] = Paths.AssetsDir(gameDir),
            ["assets_index_name"] = assetIndexId,

            ["auth_uuid"] = session.Uuid.Replace("-", ""),
            ["auth_access_token"] = session.AccessToken,
            ["auth_session"] = session.AccessToken,
            ["clientid"] = string.IsNullOrWhiteSpace(session.ClientId) ? "override-mirage" : session.ClientId,
            ["auth_xuid"] = session.Xuid,
            ["user_type"] = session.UserType,
            ["version_type"] = VersionJson.GetString(root, "type", "release"),
            ["natives_directory"] = nativesDir,
            ["launcher_name"] = "OverrideMirageLauncher",
            ["launcher_version"] = "1.0.0",
            ["classpath"] = classpath,
            ["user_properties"] = "{}",
            ["resolution_width"] = "1280",
            ["resolution_height"] = "720"
        };

        if (root.TryGetProperty("logging", out var logging) &&
            logging.TryGetProperty("client", out var clientLog) &&
            clientLog.TryGetProperty("file", out var logFile))
        {
            var logId = VersionJson.GetString(logFile, "id");
            var logPath = Path.Combine(Paths.LogConfigsDir(gameDir), logId);
            if (logId.Length > 0 && File.Exists(logPath)) vars["path"] = logPath;
        }

        var jvmArgs = new List<string>();
        var hasClasspath = false;

        if (root.TryGetProperty("arguments", out var arguments) &&
            arguments.TryGetProperty("jvm", out var jvmArray))
        {
            jvmArgs.AddRange(VersionJson.EvaluateArguments(jvmArray, vars));
            foreach (var a in jvmArgs)
            {
                if (a == "-cp" || a == "-classpath") hasClasspath = true;
            }
        }

        if (!hasClasspath)
        {
            jvmArgs.Add("-cp");
            jvmArgs.Add(classpath);
        }

        var libraryDirs = new List<string> { nativesDir };
        foreach (var sub in new[] { "java", "jna", "lwjgl", "netty" })
        {
            var dir = Path.Combine(nativesDir, sub);
            if (Directory.Exists(dir)) libraryDirs.Add(dir);
        }
        jvmArgs.Add("-Djava.library.path=" + string.Join(';', libraryDirs));

        jvmArgs.Insert(0, $"-Xmx{cfg.MemoryMb}M");
        jvmArgs.Insert(1, $"-Xms{Math.Min(cfg.MemoryMb / 2, 1024)}M");

        List<string> gameArgs;
        if (root.TryGetProperty("arguments", out arguments) &&
            arguments.TryGetProperty("game", out var gameArray))
        {
            gameArgs = VersionJson.EvaluateArguments(gameArray, vars);
        }
        else if (root.TryGetProperty("minecraftArguments", out var legacy) &&
                 legacy.GetString() is { } legacyRaw)
        {
            gameArgs = VersionJson.EvaluateLegacyArguments(legacyRaw, vars);

            if (!gameArgs.Contains("--assetsDir"))
            {
                gameArgs.Add("--assetsDir");
                gameArgs.Add(Paths.AssetsDir(gameDir));
                gameArgs.Add("--assetIndex");
                gameArgs.Add(assetIndexId);
            }
        }
        else
        {
            throw new InvalidOperationException("版本文件中没有可用的启动参数定义。");
        }

        var mainClass = VersionJson.GetString(root, "mainClass");
        if (mainClass.Length == 0)
            throw new InvalidOperationException("版本文件中缺少 mainClass。");

        var javaExe = ResolveJavaExe(java.Path);
        var psi = new ProcessStartInfo(javaExe)
        {
            WorkingDirectory = runDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var a in jvmArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(mainClass);
        foreach (var a in gameArgs) psi.ArgumentList.Add(a);

        log($"Java: {javaExe}");
        log($"主类: {mainClass}");
        log($"运行目录: {runDir}{(cfg.IsolateInstances ? "（版本隔离）" : "（共享）")}");
        log($"内存: {cfg.MemoryMb} MB");
        log($"参数数量: JVM {jvmArgs.Count} 项 / 游戏 {gameArgs.Count} 项");
        log("完整命令行: " + Quote(javaExe) + " " +
            string.Join(' ', jvmArgs.Concat(new[] { mainClass }).Concat(gameArgs).Select(Quote)));
        log($"natives 目录: {nativesDir}（{(Directory.Exists(nativesDir) ? Directory.GetFiles(nativesDir).Length : 0)} 个文件）");

        for (var i = 0; i < jvmArgs.Count; i++)
        {
            var a = jvmArgs[i];
            if (a.Length > 240) a = a[..240] + "…";
            log($"  JVM[{i}] {a}");
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) log("[游戏] " + e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log("[游戏] " + e.Data); };

        if (!process.Start())
            throw new InvalidOperationException("游戏进程未能启动。");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    private static readonly string[] RunFolders =
    {
        "saves", "mods", "config", "resourcepacks", "shaderpacks", "screenshots", "logs"
    };

    private static void EnsureRunFolders(string runDir, Action<string> log)
    {
        foreach (var name in RunFolders)
        {
            var path = Path.Combine(runDir, name);
            if (Directory.Exists(path)) continue;

            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                log($"创建 {name}\\ 失败：{ex.Message}");
            }
        }
    }

    private static string Quote(string text) =>
        text.Length == 0 || text.Contains(' ') ? "\"" + text + "\"" : text;

    private static readonly string[] LegacyFolders =
    {
        "mods", "config", "resourcepacks", "shaderpacks", "saves", "screenshots"
    };

    private static void MigrateLegacyFiles(string gameDir, string runDir, Action<string> log)
    {
        if (string.Equals(gameDir, runDir, StringComparison.OrdinalIgnoreCase)) return;
        if (!Directory.Exists(runDir)) return;
        if (Directory.EnumerateFileSystemEntries(runDir).Any()) return;

        foreach (var name in LegacyFolders)
        {
            var source = Path.Combine(gameDir, name);
            if (!Directory.Exists(source)) continue;
            if (!Directory.EnumerateFileSystemEntries(source).Any()) continue;

            var target = Path.Combine(runDir, name);
            try
            {
                Directory.Move(source, target);
                log($"已把 {name}\\ 迁移到 {runDir}");
            }
            catch (Exception ex)
            {
                log($"迁移 {name}\\ 失败：{ex.Message}（可手动把 {source} 复制到 {target}）");
            }
        }
    }

    public static string OfflineUuid(string name)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ResolveJavaExe(string javaPath)
    {

        var dir = Path.GetDirectoryName(javaPath);
        if (dir is not null)
        {
            var javaw = Path.Combine(dir, "javaw.exe");
            if (File.Exists(javaw)) return javaw;
        }
        return javaPath;
    }
}

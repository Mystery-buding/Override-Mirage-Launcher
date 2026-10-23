using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OverrideMirageLauncher.Core;

/// <summary>
/// 拼装启动参数并拉起游戏进程。
/// </summary>
public static class GameLauncher
{
    public static Process Launch(AppConfig cfg, ResolvedVersion resolved, JavaInfo java, Action<string> log,
        AuthSession? auth = null)
    {
        var session = auth ?? cfg.CreateSession();
        var gameDir = cfg.GameDir;
        var versionId = resolved.VersionId;
        var versionDir = Path.Combine(Paths.VersionsDir(gameDir), versionId);

        // 版本隔离：游戏进程的工作目录（mods / 存档 / 配置）每个整合包一份，
        // 版本、依赖库、资源仍然共用 gameDir，不会重复下载。
        var runDir = cfg.RunDir(versionId);
        Directory.CreateDirectory(runDir);
        MigrateLegacyFiles(gameDir, runDir, log);
        EnsureRunFolders(runDir, log);

        using var doc = System.Text.Json.JsonDocument.Parse(resolved.MergedJson);
        var root = doc.RootElement;

        // ---------- classpath ----------
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

        // ---------- 基础变量 ----------
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
            // 官方启动器传的是 32 位无连字符 UUID
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

        // log4j 配置路径
        if (root.TryGetProperty("logging", out var logging) &&
            logging.TryGetProperty("client", out var clientLog) &&
            clientLog.TryGetProperty("file", out var logFile))
        {
            var logId = VersionJson.GetString(logFile, "id");
            var logPath = Path.Combine(Paths.LogConfigsDir(gameDir), logId);
            if (logId.Length > 0 && File.Exists(logPath)) vars["path"] = logPath;
        }

        // ---------- JVM 参数 ----------
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

        // 本地库搜索路径统一在末尾追加，覆盖版本 JSON 的默认值。
        // 新版（1.21+）版本 JSON 会写成 ${natives_directory}/java、/jna、/lwjgl、/netty，
        // 把本地库拆到子目录里；这里同时包含根目录与各子目录，两种布局都能命中。
        var libraryDirs = new List<string> { nativesDir };
        foreach (var sub in new[] { "java", "jna", "lwjgl", "netty" })
        {
            var dir = Path.Combine(nativesDir, sub);
            if (Directory.Exists(dir)) libraryDirs.Add(dir);
        }
        jvmArgs.Add("-Djava.library.path=" + string.Join(';', libraryDirs));

        jvmArgs.Insert(0, $"-Xmx{cfg.MemoryMb}M");
        jvmArgs.Insert(1, $"-Xms{Math.Min(cfg.MemoryMb / 2, 1024)}M");

        // ---------- 游戏参数 ----------
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

            // 老版本没有资源参数时补上
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

        // ---------- 启动 ----------
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

    /// <summary>整合包运行目录里游戏自己会用到、但有时不会主动建的子目录。</summary>
    private static readonly string[] RunFolders =
    {
        "saves", "mods", "config", "resourcepacks", "shaderpacks", "screenshots", "logs"
    };

    /// <summary>
    /// 先把存档 / 模组这些目录建出来，省得游戏第一次写存档时因为目录不存在而失败。
    /// 已经存在就什么都不做。
    /// </summary>
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

    /// <summary>日志里的命令行要可直接复制运行，所以每个参数都加引号。</summary>
    private static string Quote(string text) =>
        text.Length == 0 || text.Contains(' ') ? "\"" + text + "\"" : text;

    /// <summary>开启版本隔离后要搬进独立目录的旧共享目录。</summary>
    private static readonly string[] LegacyFolders =
    {
        "mods", "config", "resourcepacks", "shaderpacks", "saves", "screenshots"
    };

    /// <summary>
    /// 第一次为某个整合包创建独立目录时，把原来堆在 .minecraft 根目录里的
    /// mods / 存档 / 配置搬进去，避免开隔离后旧内容「消失」。
    /// 已经搬过（目录不存在或为空）就什么都不做。
    /// </summary>
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

    /// <summary>
    /// 离线模式 UUID：MD5("OfflinePlayer:&lt;name&gt;")，按 UUID v3 规则置位。
    /// </summary>
    public static string OfflineUuid(string name)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ResolveJavaExe(string javaPath)
    {
        // 优先用 javaw，避免多一个控制台窗口
        var dir = Path.GetDirectoryName(javaPath);
        if (dir is not null)
        {
            var javaw = Path.Combine(dir, "javaw.exe");
            if (File.Exists(javaw)) return javaw;
        }
        return javaPath;
    }
}

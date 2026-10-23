using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace OverrideMirageLauncher.Core;

/// <summary>
/// 命令行模式，便于自动化验证：
///   --install &lt;版本号&gt;
///   --smokelaunch &lt;版本号&gt; [用户名]
/// </summary>
public static class CliRunner
{
    public static async Task<int> InstallAsync(string versionId)
    {
        var cfg = LoadConfig();
        LogBus.Info($"[CLI] 开始安装 {versionId} → {cfg.GameDir}");

        var lastReported = -10;
        var progress = new Progress<InstallProgress>(p =>
        {
            var percent = (int)p.Percent;
            if (percent - lastReported >= 5 || percent >= 100)
            {
                lastReported = percent;
                LogBus.Info($"[CLI] {percent}% {p.Stage}");
            }
        });

        try
        {
            await MinecraftInstaller.InstallAsync(cfg, versionId, progress, CancellationToken.None)
                .ConfigureAwait(false);
            LogBus.Info("[CLI] 安装结束");
            return 0;
        }
        catch (Exception ex)
        {
            LogBus.Error("[CLI] 安装异常：" + ex);
            return 1;
        }
    }

    /// <summary>
    /// 命令行安装模组加载器：--installloader &lt;Fabric|Quilt|Forge|NeoForge&gt; &lt;原版&gt; &lt;加载器版本&gt;
    /// </summary>
    public static async Task<int> InstallLoaderAsync(string kindName, string mcVersion, string loaderVersion)
    {
        if (!Enum.TryParse<LoaderKind>(kindName, true, out var kind))
        {
            LogBus.Error($"[CLI] 未知的加载器类型：{kindName}");
            return 1;
        }

        var cfg = LoadConfig();
        LogBus.Info($"[CLI] 安装 {LoaderService.DisplayName(kind)} {loaderVersion} 到 {mcVersion}");

        var lastReported = -10;
        var progress = new Progress<InstallProgress>(p =>
        {
            var percent = (int)p.Percent;
            if (percent - lastReported >= 5 || percent >= 100)
            {
                lastReported = percent;
                LogBus.Info($"[CLI] {percent}% {p.Stage}");
            }
        });

        try
        {
            var id = await LoaderService.InstallAsync(cfg, kind, mcVersion, loaderVersion,
                progress, LogBus.Info, CancellationToken.None).ConfigureAwait(false);
            LogBus.Info($"[CLI] 加载器安装完成，版本 id = {id}");
            return 0;
        }
        catch (Exception ex)
        {
            LogBus.Error("[CLI] 加载器安装异常：" + ex);
            return 1;
        }
    }

    /// <summary>
    /// 正版登录诊断（--mslogin）：走完整的设备码授权 → Xbox → XSTS → Minecraft → 档案，
    /// 每一步的结果都写进 exe 同级的 mslogin.log 并自动打开，用来定位「浏览器授权成功但登录失败」。
    /// 成功后账号直接写进 config.json。
    /// </summary>
    public static async Task<int> MicrosoftLoginTestAsync()
    {
        var cfg = LoadConfig();
        var clientId = cfg.EffectiveMsClientId;
        var logPath = Path.Combine(Paths.BaseDir, "mslogin.log");
        var lines = new List<string>();

        void Trace(string text)
        {
            lines.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
            LogBus.Info("[mslogin] " + text);
            File.WriteAllLines(logPath, lines);
        }

        if (!cfg.HasMsClientId)
        {
            Trace("尚未配置 Azure 应用 ID，无法诊断正版登录。");
            Trace("请到 https://portal.azure.com → 应用注册 新建应用（身份验证里打开「允许公共客户端流」），");
            Trace("把概览页的「应用程序(客户端) ID」填进启动器的「设置 → 应用 ID」，再重新执行 --mslogin。");
            Trace("本启动器不内置任何应用 ID，也不会上传你的账号信息。");
            try { Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true }); }
            catch { /* 打不开就算了 */ }
            Console.Error.WriteLine("尚未配置 Azure 应用 ID，详见 mslogin.log");
            return 1;
        }

        lines.Add("Override Mirage Launcher —— 正版登录诊断");
        lines.Add($"应用 ID：{clientId}（自定义）");
        File.WriteAllLines(logPath, lines);

        // 日志文件直接打开，用户能一边授权一边看进展
        try { Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true }); }
        catch { /* 打不开就算了，用户自己找文件 */ }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(16));

        DeviceCodeInfo device;
        try
        {
            device = await MicrosoftAuth.StartDeviceCodeAsync(clientId, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace("申请设备码失败：" + ex.Message);
            Trace("→ 检查 Azure 应用是否开启「允许公共客户端流」、登录受众是否包含个人微软账号。");
            return 1;
        }

        Trace($"请在浏览器打开 {device.VerificationUri}");
        Trace($"输入代码：{device.UserCode}（{device.ExpiresInSeconds / 60} 分钟内有效）");
        Trace("已尝试自动打开浏览器，等待你完成授权…");

        try { Process.Start(new ProcessStartInfo(device.VerificationUri) { UseShellExecute = true }); }
        catch { /* 忽略 */ }

        try
        {
            var account = await MicrosoftAuth.WaitForLoginAsync(
                    clientId, device, new Progress<string>(Trace), cts.Token, Trace)
                .ConfigureAwait(false);

            cfg.MsName = account.Name;
            cfg.MsUuid = account.Uuid;
            cfg.MsAccessToken = account.AccessToken;
            cfg.MsRefreshToken = account.RefreshToken;
            cfg.MsExpiresAt = account.ExpiresAt;
            cfg.AuthMode = "microsoft";
            cfg.Save();

            Trace($"登录成功：{account.Name}（UUID {account.Uuid}），已写入 config.json");
            return 0;
        }
        catch (Exception ex)
        {
            Trace("登录失败：" + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// 清掉本机的正版账号凭证（--resetaccount）。
    /// 账号只存在 config.json，不写进程序；要把启动器拷给别人之前跑一次（或直接删掉 config.json），
    /// 对方打开就是「未登录」，登的是他自己的微软账号。
    /// </summary>
    public static int ResetAccount()
    {
        var cfg = LoadConfig();

        cfg.MsName = "";
        cfg.MsUuid = "";
        cfg.MsAccessToken = "";
        cfg.MsRefreshToken = "";
        cfg.MsXuid = "";
        cfg.MsExpiresAt = default;
        cfg.AuthMode = "offline";
        cfg.Save();

        LogBus.Info("[CLI] 已清除本机正版账号，登录方式回到离线");
        return 0;
    }

    /// <summary>
    /// 一体化安装：--combo &lt;原版&gt; &lt;vanilla|Fabric|Quilt|Forge|NeoForge&gt; [加载器版本] [名称]
    /// 与界面上的「安装」按钮走同一条 <see cref="CombinedInstaller"/> 路径。
    /// </summary>
    public static async Task<int> ComboAsync(string[] args)
    {
        var mc = args[0];

        LoaderKind? kind = null;
        string? loaderVersion = null;

        if (args.Length > 1 && !args[1].Equals("vanilla", StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse<LoaderKind>(args[1], true, out var parsed))
            {
                LogBus.Error($"[CLI] 未知的加载器类型：{args[1]}");
                return 1;
            }

            kind = parsed;
            loaderVersion = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : null;

            // 没给版本就取最新的一个
            if (string.IsNullOrWhiteSpace(loaderVersion))
            {
                var list = await LoaderService.GetLoaderVersionsAsync(parsed, mc, CancellationToken.None)
                    .ConfigureAwait(false);
                loaderVersion = list.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(loaderVersion))
                {
                    LogBus.Error($"[CLI] {LoaderService.DisplayName(parsed)} 在 {mc} 上没有可用版本");
                    return 1;
                }
            }
        }

        var name = args.Length > 3 && !args[3].StartsWith("--") ? args[3] : null;

        var cfg = LoadConfig();
        var label = CombinedInstaller.Describe(mc, kind, loaderVersion);
        LogBus.Info($"[CLI] 一体化安装 {label} → {cfg.GameDir}");

        var lastReported = -10;
        var progress = new Progress<InstallProgress>(p =>
        {
            var percent = (int)p.Percent;
            if (percent - lastReported >= 5 || percent >= 100)
            {
                lastReported = percent;
                LogBus.Info($"[CLI] {percent}% {p.Stage}");
            }
        });

        try
        {
            var id = await CombinedInstaller.InstallAsync(cfg, mc, kind, loaderVersion, name,
                progress, LogBus.Info, CancellationToken.None).ConfigureAwait(false);
            LogBus.Info($"[CLI] {label} 安装完成，整合包 id = {id}");
            return 0;
        }
        catch (Exception ex)
        {
            LogBus.Error($"[CLI] 一体化安装异常：" + ex);
            return 1;
        }
    }

    /// <summary>
    /// 真实拉起游戏，观察 25 秒后判定是否启动成功，然后结束进程。
    /// </summary>
    public static int SmokeLaunch(string versionId, string? playerName)
    {
        var cfg = LoadConfig();
        if (!string.IsNullOrWhiteSpace(playerName)) cfg.PlayerName = playerName;

        var versionDir = Path.Combine(Paths.VersionsDir(cfg.GameDir), versionId);
        var jsonPath = Path.Combine(versionDir, versionId + ".json");

        if (!File.Exists(jsonPath))
        {
            LogBus.Error($"[CLI] {versionId} 未安装，请先执行 --install");
            return 1;
        }

        var resolved = VersionResolver.ResolveAsync(cfg.GameDir, versionId).GetAwaiter().GetResult();

        // 旧版本（或手动拷过来的）NeoForge 可能缺运行依赖，启动前顺手补一次
        if (NeoForgeRuntime.EnsureAsync(cfg.GameDir, versionId, resolved.MergedJson, LogBus.Info)
                .GetAwaiter().GetResult())
            resolved = VersionResolver.ResolveAsync(cfg.GameDir, versionId).GetAwaiter().GetResult();

        var jarPath = Path.Combine(Paths.VersionsDir(cfg.GameDir), resolved.JarId, resolved.JarId + ".jar");
        if (!File.Exists(jarPath))
        {
            LogBus.Error($"[CLI] 缺少客户端 jar：{resolved.JarId}.jar，请先执行 --install");
            return 1;
        }

        var required = 8;
        try
        {
            // 必须读合并后的 JSON：加载器版本自身通常没有 javaVersion，
            // 只看子版本会回落成 8，进而挑到 Java 8 启动失败。
            using var doc = JsonDocument.Parse(resolved.MergedJson);
            required = VersionJson.GetRequiredJavaMajor(doc.RootElement);
        }
        catch
        {
            // 忽略
        }

        var javas = JavaFinder.Discover(cfg.GameDir);
        var java = JavaFinder.Pick(javas, required, out var satisfied);
        if (java is null)
        {
            LogBus.Error("[CLI] 没有可用的 Java");
            return 1;
        }

        if (!satisfied)
            LogBus.Warn($"[CLI] 没有满足 Java {required} 的运行时，退而使用 Java {java.MajorVersion}");

        try
        {
            var process = GameLauncher.Launch(cfg, resolved, java, LogBus.Info);
            LogBus.Info($"[CLI] 游戏进程已启动，PID {process.Id}，观察 25 秒…");

            if (process.WaitForExit(25000))
            {
                LogBus.Error($"[CLI] 进程提前退出，退出码 {process.ExitCode}");
                return 1;
            }

            LogBus.Info("[CLI] 25 秒内进程保持运行，判定启动成功");
            try { process.Kill(true); } catch { /* 忽略 */ }
            return 0;
        }
        catch (Exception ex)
        {
            LogBus.Error("[CLI] 启动异常：" + ex);
            return 1;
        }
    }

    private static AppConfig LoadConfig()
    {
        var cfg = AppConfig.Load();
        if (string.IsNullOrWhiteSpace(cfg.GameDir))
            cfg.GameDir = Path.Combine(Paths.BaseDir, ".minecraft");
        return cfg;
    }
}

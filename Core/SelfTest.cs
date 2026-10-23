using System.IO;
using System.Text;
using System.Text.Json;

namespace OverrideMirageLauncher.Core;

/// <summary>
/// 无界面自检：验证网络、版本解析、依赖筛选、Java 探测是否正常。
/// 运行方式：OverrideMirageLauncher.exe --selftest
/// 结果写入 exe 同目录的 selftest.log。
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        var sb = new StringBuilder();
        var code = 0;

        try
        {
            sb.AppendLine("=========== Override Mirage Launcher 自检 ===========");
            sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"目录: {Paths.BaseDir}");
            sb.AppendLine();

            var manifest = await VersionService.GetManifestAsync(true).ConfigureAwait(false);
            sb.AppendLine($"[1] 版本清单: {manifest.Versions.Count} 个版本，最新正式版 = {manifest.Latest?.Release}");

            var targetId = manifest.Latest?.Release ?? manifest.Versions.First().Id;
            var (doc, _) = await VersionService.GetVersionJsonAsync(targetId).ConfigureAwait(false);
            var root = doc.RootElement;

            sb.AppendLine($"[2] 选中版本: {targetId}");
            sb.AppendLine($"    mainClass      = {VersionJson.GetString(root, "mainClass")}");
            sb.AppendLine($"    需要 Java      = {VersionJson.GetRequiredJavaMajor(root)}");
            sb.AppendLine($"    类型           = {VersionJson.GetString(root, "type")}");

            var libraries = MinecraftInstaller.CollectLibraries(root);
            sb.AppendLine($"[3] 依赖库: {libraries.Count} 个，其中 natives {libraries.Count(l => l.IsNative)} 个");

            var assetIndex = root.TryGetProperty("assetIndex", out var ai)
                ? VersionJson.GetString(ai, "id") : "(无)";
            sb.AppendLine($"[4] 资源索引: {assetIndex}");

            var gameArgs = root.TryGetProperty("arguments", out var args) &&
                           args.TryGetProperty("game", out var ga)
                ? ga.GetArrayLength() : -1;
            sb.AppendLine($"[5] game 参数条目: {(gameArgs >= 0 ? gameArgs.ToString() : "旧格式 minecraftArguments")}");

            sb.AppendLine($"[6] 离线 UUID: {GameLauncher.OfflineUuid("TestPlayer")}");

            var javas = JavaFinder.Discover(Paths.BaseDir);
            sb.AppendLine($"[7] 发现 Java: {javas.Count} 个");
            foreach (var j in javas.Take(6))
                sb.AppendLine($"      - Java {j.MajorVersion}: {j.Path}");

            await TestContentSourcesAsync(sb).ConfigureAwait(false);
            await CheckMicrosoftChannelAsync(sb).ConfigureAwait(false);

            doc.Dispose();
            sb.AppendLine();
            sb.AppendLine("自检结束：通过");
        }
        catch (Exception ex)
        {
            sb.AppendLine();
            sb.AppendLine("自检失败：" + ex);
            code = 1;
        }

        try
        {
            File.WriteAllText(Path.Combine(Paths.BaseDir, "selftest.log"), sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 忽略
        }

        return code;
    }

    /// <summary>
    /// 模组 / 资源包 / 光影三个来源的链路自检：搜索 → 取版本 → 真下一个文件到临时目录。
    /// 临时目录用完即删，不会往整合包里塞东西。
    /// </summary>
    private static async Task TestContentSourcesAsync(StringBuilder sb)
    {
        sb.AppendLine("[8] Modrinth 内容源：");

        var temp = Path.Combine(Path.GetTempPath(), "override-mirage-content-test");

        foreach (var (kind, query) in new[]
                 {
                     (ContentKind.Mod, "jei"),
                     (ContentKind.ResourcePack, "faithful"),
                     (ContentKind.Shader, "bsl")
                 })
        {
            var name = ContentTypes.DisplayName(kind);
            try
            {
                var projects = await ModrinthService.SearchAsync(query, kind, null, null, 5)
                    .ConfigureAwait(false);
                if (projects.Count == 0)
                {
                    sb.AppendLine($"      {name}：搜索「{query}」没有结果");
                    continue;
                }

                var top = projects[0];
                sb.AppendLine($"      {name}：{projects.Count} 个结果，第一个 = {top.Title}（{top.Author}）");

                var versions = await ModrinthService.GetVersionsAsync(top.Id, null, null)
                    .ConfigureAwait(false);
                sb.AppendLine($"          版本 {versions.Count} 个，最新 = {versions.FirstOrDefault()?.VersionNumber ?? "无"}");

                var latest = versions.FirstOrDefault(v => v.Files.Count > 0);
                if (latest is null) continue;

                var path = await ModrinthService.DownloadAsync(latest, temp).ConfigureAwait(false);
                var size = new FileInfo(path).Length;
                sb.AppendLine($"          下载 {Path.GetFileName(path)}（{size / 1024.0:0.0} KB）→ {(size > 0 ? "成功" : "空文件")}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"      {name}：失败 —— {ex.Message}");
            }
        }

        // CurseForge 需要 API Key，没填就只提示而不算失败
        var cfg = AppConfig.Load();
        if (cfg.CfApiKey.Length == 0)
        {
            sb.AppendLine("      CurseForge：未填 API Key，跳过（设置页填写后这里会自动测）");
        }
        else
        {
            try
            {
                var cf = await CurseForgeService.SearchAsync("jei", ContentKind.Mod, null, null, cfg.CfApiKey, 5)
                    .ConfigureAwait(false);
                sb.AppendLine($"      CurseForge：{cf.Count} 个结果，第一个 = {cf.FirstOrDefault()?.Title ?? "无"}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"      CurseForge：失败 —— {ex.Message}");
            }
        }

        try
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
        catch
        {
            // 临时目录删不掉也不影响
        }
    }

    /// <summary>
    /// [9] 正版登录通道：用内置（或用户配置的）Azure 应用 ID 申请一次设备码。
    /// 只验证「这个应用能发起授权」，不会也不需要真的登录，不碰任何账号信息。
    /// </summary>
    private static async Task CheckMicrosoftChannelAsync(StringBuilder sb)
    {
        var cfg = AppConfig.Load();
        var clientId = cfg.EffectiveMsClientId;

        sb.AppendLine();
        if (!cfg.HasMsClientId)
        {
            sb.AppendLine("[9] 正版登录通道：未配置 Azure 应用 ID（本仓库不内置任何应用 ID），跳过设备码检查");
            return;
        }
        sb.AppendLine($"[9] 正版登录通道：应用 ID = {MaskId(clientId)}（用户配置）");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var device = await MicrosoftAuth.StartDeviceCodeAsync(clientId, cts.Token).ConfigureAwait(false);

            if (device.UserCode.Length == 0 || device.DeviceCode.Length == 0)
            {
                sb.AppendLine("      !! 设备码为空");
                return;
            }

            sb.AppendLine($"      设备码申请成功：{device.VerificationUri}（{device.ExpiresInSeconds / 60} 分钟内有效，本次不完成授权）");
            sb.AppendLine($"      本机正版账号：{(cfg.HasMicrosoftAccount ? "已登录 " + cfg.MsName : "未登录")}" +
                          "（账号只存在 config.json，不随程序分发）");
        }
        catch (Exception ex)
        {
            sb.AppendLine("      !! 设备码申请失败：" + ex.Message);
            sb.AppendLine("         检查 Azure 应用是否开启「允许公共客户端流」、登录受众是否包含个人微软账号。");
        }
    }

    private static string MaskId(string id) =>
        id.Length <= 8 ? id : id[..8] + "…" + id[^4..];
}

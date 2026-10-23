namespace OverrideMirageLauncher.Core;

/// <summary>
/// 一体化安装：Minecraft 本体（+ 可选模组加载器）一次装完，只产出 1 个整合包。
///
/// 装加载器时，原版文件由 <see cref="LoaderService"/> 内部作为依赖底座一起补齐，
/// 用户不需要、也看不到「先装本体再装加载器」这两步；底座会被整合包列表自动隐藏。
/// 界面与命令行（--combo）共用这一条路径。
/// </summary>
public static class CombinedInstaller
{
    /// <summary>「这次装的是什么」的一句话描述，用于进度与日志。</summary>
    public static string Describe(string mcVersion, LoaderKind? kind, string? loaderVersion) =>
        kind is { } k
            ? $"Minecraft {mcVersion} + {LoaderService.DisplayName(k)} {loaderVersion}"
            : $"Minecraft {mcVersion}";

    /// <summary>
    /// 一次装完。返回最终整合包 id（需要改名时已改好）。
    /// packName 为空 / 与默认 id 相同 / 不可用时都保留安装器给的默认 id。
    /// </summary>
    public static async Task<string> InstallAsync(AppConfig cfg, string mcVersion, LoaderKind? kind,
        string? loaderVersion, string? packName, IProgress<InstallProgress>? progress, Action<string> log,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mcVersion))
            throw new ArgumentException("没有指定要安装的 Minecraft 版本。");

        var label = Describe(mcVersion, kind, loaderVersion);
        log($"开始安装 {label}");

        string id;
        if (kind is { } k)
        {
            if (string.IsNullOrWhiteSpace(loaderVersion))
                throw new ArgumentException($"选择了 {LoaderService.DisplayName(k)} 但没有指定版本。");

            id = await LoaderService.InstallAsync(cfg, k, mcVersion, loaderVersion!, progress, log, ct)
                .ConfigureAwait(false);
        }
        else
        {
            await MinecraftInstaller.InstallAsync(cfg, mcVersion, progress, ct).ConfigureAwait(false);
            id = mcVersion;
        }

        var name = packName?.Trim();
        if (string.IsNullOrEmpty(name) || string.Equals(name, id, StringComparison.OrdinalIgnoreCase))
            return id;

        var error = PackNaming.Validate(name, cfg.GameDir);
        if (error is not null)
        {
            log($"名称「{name}」不可用（{error}），保留 {id}");
            return id;
        }

        return await PackNaming.RenameAsync(cfg.GameDir, id, name, ct).ConfigureAwait(false);
    }
}

namespace OverrideMirageLauncher.Core;

public static class CombinedInstaller
{

    public static string Describe(string mcVersion, LoaderKind? kind, string? loaderVersion) =>
        kind is { } k
            ? $"Minecraft {mcVersion} + {LoaderService.DisplayName(k)} {loaderVersion}"
            : $"Minecraft {mcVersion}";

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

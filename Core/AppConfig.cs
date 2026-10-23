using System.IO;
using System.Text.Json;

namespace OverrideMirageLauncher.Core;

/// <summary>
/// 启动器配置。存放在 exe 同目录的 config.json，便携式设计。
/// </summary>
public sealed class AppConfig
{
    public string PlayerName { get; set; } = "Player";
    public string VersionId { get; set; } = "";
    public string GameDir { get; set; } = "";
    public string JavaPath { get; set; } = "";
    public int MemoryMb { get; set; } = 4096;

    /// <summary>下载源：false = 官方，true = BMCLAPI 镜像。</summary>
    public bool UseMirror { get; set; }

    /// <summary>
    /// CurseForge 的第三方 API Key（api.curseforge.com 强制要求）。
    /// 留空则下载页的 CurseForge 来源不可用，仍可用 Modrinth。
    /// </summary>
    public string CfApiKey { get; set; } = "";

    /// <summary>
    /// 版本隔离：每个整合包用自己的目录放 mods / 存档 / 配置，
    /// 关闭则退回所有整合包共用 .minecraft 根目录（旧行为）。
    /// </summary>
    public bool IsolateInstances { get; set; } = true;

    /// <summary>该整合包实际使用的游戏运行目录（隔离开启时是 instances/&lt;id&gt;）。</summary>
    public string RunDir(string versionId) =>
        IsolateInstances && !string.IsNullOrWhiteSpace(versionId)
            ? Paths.InstanceDir(GameDir, versionId)
            : GameDir;

    /// <summary>该整合包的 mods 目录。</summary>
    public string ModsDir(string versionId) => Path.Combine(RunDir(versionId), "mods");

    // ---- 登录 ----
    /// <summary>offline（盗版/离线）或 microsoft（正版）。</summary>
    public string AuthMode { get; set; } = "offline";

    /// <summary>
    /// 用户自己在 Azure 门户注册的「移动和桌面应用程序」的客户端 ID（GUID）。
    /// 本仓库不内置任何应用 ID——内置别人的 ID 会让该 ID 名下的流量与风险算到登记者头上。
    /// 注意：这里只放「应用」的 ID，账号凭证（下面的 Ms* 字段）永远只属于本机登录的那个人。
    /// </summary>
    public string MsClientId { get; set; } = "";

    /// <summary>实际用于登录 / 续期的客户端 ID（就是用户配置的那个，没有内置兜底）。</summary>
    public string EffectiveMsClientId => MicrosoftAuth.ResolveClientId(MsClientId);

    /// <summary>是否已配置可用的 Azure 应用 ID。</summary>
    public bool HasMsClientId => MicrosoftAuth.HasClientId(MsClientId);
    public string MsName { get; set; } = "";
    public string MsUuid { get; set; } = "";
    public string MsAccessToken { get; set; } = "";
    public string MsRefreshToken { get; set; } = "";
    public string MsXuid { get; set; } = "";
    public DateTimeOffset MsExpiresAt { get; set; }

    public bool HasMicrosoftAccount =>
        !string.IsNullOrWhiteSpace(MsAccessToken) && !string.IsNullOrWhiteSpace(MsUuid);

    /// <summary>按当前登录方式生成启动会话；正版信息缺失时回落到离线。</summary>
    public AuthSession CreateSession() =>
        AuthMode == "microsoft" && HasMicrosoftAccount
            ? AuthSession.Microsoft(MsName, MsUuid, MsAccessToken, MsXuid, EffectiveMsClientId)
            : AuthSession.Offline(string.IsNullOrWhiteSpace(PlayerName) ? "Player" : PlayerName);

    private static string ConfigPath => Path.Combine(Paths.BaseDir, "config.json");

    public static AppConfig Load()
    {
        var cfg = new AppConfig();
        try
        {
            if (File.Exists(ConfigPath))
            {
                var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath));
                if (loaded is not null) cfg = loaded;
            }
        }
        catch
        {
            // 配置损坏时回落到默认值，不阻断启动
        }

        if (string.IsNullOrWhiteSpace(cfg.GameDir))
            cfg.GameDir = Path.Combine(Paths.BaseDir, ".minecraft");
        if (string.IsNullOrWhiteSpace(cfg.PlayerName))
            cfg.PlayerName = "Player";
        if (cfg.MemoryMb < 512) cfg.MemoryMb = 4096;

        return cfg;
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // 保存失败不影响本次运行
        }
    }
}

public static class Paths
{
    public static string BaseDir => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    public static string VersionsDir(string gameDir) => Path.Combine(gameDir, "versions");

    /// <summary>版本隔离目录的根：&lt;gameDir&gt;/instances。</summary>
    public static string InstancesDir(string gameDir) => Path.Combine(gameDir, "instances");

    /// <summary>某个整合包独立使用的游戏运行目录：&lt;gameDir&gt;/instances/&lt;id&gt;。</summary>
    public static string InstanceDir(string gameDir, string versionId) =>
        Path.Combine(InstancesDir(gameDir), versionId);
    public static string LibrariesDir(string gameDir) => Path.Combine(gameDir, "libraries");
    public static string AssetsDir(string gameDir) => Path.Combine(gameDir, "assets");
    public static string IndexesDir(string gameDir) => Path.Combine(AssetsDir(gameDir), "indexes");
    public static string ObjectsDir(string gameDir) => Path.Combine(AssetsDir(gameDir), "objects");
    public static string LogConfigsDir(string gameDir) => Path.Combine(AssetsDir(gameDir), "log_configs");
    public static string RuntimeDir(string gameDir) => Path.Combine(gameDir, "runtime");
}

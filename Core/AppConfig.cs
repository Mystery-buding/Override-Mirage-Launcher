using System.IO;
using System.Text.Json;

namespace OverrideMirageLauncher.Core;

public sealed class AppConfig
{
    public string PlayerName { get; set; } = "Player";
    public string VersionId { get; set; } = "";
    public string GameDir { get; set; } = "";
    public string JavaPath { get; set; } = "";
    public int MemoryMb { get; set; } = 4096;

    public bool UseMirror { get; set; }

    public string CfApiKey { get; set; } = "";

    public bool IsolateInstances { get; set; } = true;

    public string RunDir(string versionId) =>
        IsolateInstances && !string.IsNullOrWhiteSpace(versionId)
            ? Paths.InstanceDir(GameDir, versionId)
            : GameDir;

    public string ModsDir(string versionId) => Path.Combine(RunDir(versionId), "mods");

    public string AuthMode { get; set; } = "offline";

    public string MsClientId { get; set; } = "";

    public string EffectiveMsClientId => MicrosoftAuth.ResolveClientId(MsClientId);

    public bool HasMsClientId => MicrosoftAuth.HasClientId(MsClientId);
    public string MsName { get; set; } = "";
    public string MsUuid { get; set; } = "";
    public string MsAccessToken { get; set; } = "";
    public string MsRefreshToken { get; set; } = "";
    public string MsXuid { get; set; } = "";
    public DateTimeOffset MsExpiresAt { get; set; }

    public bool HasMicrosoftAccount =>
        !string.IsNullOrWhiteSpace(MsAccessToken) && !string.IsNullOrWhiteSpace(MsUuid);

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

        }
    }
}

public static class Paths
{
    public static string BaseDir => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    public static string VersionsDir(string gameDir) => Path.Combine(gameDir, "versions");

    public static string InstancesDir(string gameDir) => Path.Combine(gameDir, "instances");

    public static string InstanceDir(string gameDir, string versionId) =>
        Path.Combine(InstancesDir(gameDir), versionId);
    public static string LibrariesDir(string gameDir) => Path.Combine(gameDir, "libraries");
    public static string AssetsDir(string gameDir) => Path.Combine(gameDir, "assets");
    public static string IndexesDir(string gameDir) => Path.Combine(AssetsDir(gameDir), "indexes");
    public static string ObjectsDir(string gameDir) => Path.Combine(AssetsDir(gameDir), "objects");
    public static string LogConfigsDir(string gameDir) => Path.Combine(AssetsDir(gameDir), "log_configs");
    public static string RuntimeDir(string gameDir) => Path.Combine(gameDir, "runtime");
}

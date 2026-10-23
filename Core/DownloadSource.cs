namespace OverrideMirageLauncher.Core;

public static class DownloadSource
{
    private const string MirrorBase = "https://bmclapi2.bangbang93.com";

    private static readonly (string From, string To)[] Rules =
    {
        ("https://piston-meta.mojang.com", MirrorBase),
        ("https://piston-data.mojang.com", MirrorBase),
        ("https://launchermeta.mojang.com", MirrorBase),
        ("https://launcher.mojang.com", MirrorBase),
        ("https://libraries.minecraft.net", MirrorBase + "/maven"),
        ("https://resources.download.minecraft.net", MirrorBase + "/assets"),
        ("https://meta.fabricmc.net", MirrorBase + "/fabric-meta"),
        ("https://maven.minecraftforge.net", MirrorBase + "/maven"),
        ("https://maven.neoforged.net/releases", MirrorBase + "/maven")
    };

    public static bool UseMirror { get; private set; }

    public static void Configure(bool useMirror) => UseMirror = useMirror;

    public static IDisposable PreferMirror() => new MirrorScope();

    private sealed class MirrorScope : IDisposable
    {
        private readonly bool _previous;

        public MirrorScope()
        {
            _previous = UseMirror;
            UseMirror = true;
        }

        public void Dispose() => UseMirror = _previous;
    }

    public static string Rewrite(string url)
    {
        if (!UseMirror || string.IsNullOrEmpty(url)) return url;

        foreach (var (from, to) in Rules)
        {
            if (url.StartsWith(from, StringComparison.OrdinalIgnoreCase))
                return to + url[from.Length..];
        }

        return url;
    }
}

using System.IO;

namespace OverrideMirageLauncher.Core;

public enum ContentKind
{
    Mod,
    ResourcePack,
    Shader
}

public enum ContentProvider
{
    Modrinth,
    CurseForge
}

public sealed record ContentProject(
    string Id,
    ContentProvider Provider,
    string Title,
    string Description,
    string Author,
    string? IconUrl,
    int Downloads,
    string[] Categories,
    string[] GameVersions);

public sealed record ContentFile(string Url, string FileName, string? Sha1, long Size, bool Primary);

public sealed record ContentVersion(
    string Id,
    ContentProvider Provider,
    string Name,
    string VersionNumber,
    string[] GameVersions,
    string[] Loaders,
    string VersionType,
    DateTimeOffset Published,
    IReadOnlyList<ContentFile> Files);

public static class ContentTypes
{
    private const string ModrinthType = "modrinth";
    private const string CurseForgeType = "curseforge";

    public static string ProviderName(ContentProvider provider) =>
        provider == ContentProvider.CurseForge ? "CurseForge" : "Modrinth";

    public static string ProviderKey(ContentProvider provider) =>
        provider == ContentProvider.CurseForge ? CurseForgeType : ModrinthType;

    public static string DisplayName(ContentKind kind) => kind switch
    {
        ContentKind.ResourcePack => "资源包",
        ContentKind.Shader => "光影",
        _ => "模组"
    };

    public static string FolderName(ContentKind kind) => kind switch
    {
        ContentKind.ResourcePack => "resourcepacks",
        ContentKind.Shader => "shaderpacks",
        _ => "mods"
    };

    public static string TargetDir(AppConfig cfg, string versionId, ContentKind kind) =>
        Path.Combine(cfg.RunDir(versionId), FolderName(kind));
}

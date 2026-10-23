using System.IO;

namespace OverrideMirageLauncher.Core;

/// <summary>下载页「模组 / 资源包 / 光影」三个来源对应的内容类型。</summary>
public enum ContentKind
{
    Mod,
    ResourcePack,
    Shader
}

/// <summary>内容来源：Modrinth（免 Key）或 CurseForge（需要 API Key）。</summary>
public enum ContentProvider
{
    Modrinth,
    CurseForge
}

/// <summary>搜索结果里的一个项目。</summary>
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

/// <summary>项目的某个具体版本（可以直接下载的文件）。</summary>
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

/// <summary>内容类型与来源的公共信息。</summary>
public static class ContentTypes
{
    private const string ModrinthType = "modrinth";
    private const string CurseForgeType = "curseforge";

    public static string ProviderName(ContentProvider provider) =>
        provider == ContentProvider.CurseForge ? "CurseForge" : "Modrinth";

    public static string ProviderKey(ContentProvider provider) =>
        provider == ContentProvider.CurseForge ? CurseForgeType : ModrinthType;

    /// <summary>内容类型 → 中文名。</summary>
    public static string DisplayName(ContentKind kind) => kind switch
    {
        ContentKind.ResourcePack => "资源包",
        ContentKind.Shader => "光影",
        _ => "模组"
    };

    /// <summary>内容类型 → 应放到的目录名（相对整合包运行目录）。</summary>
    public static string FolderName(ContentKind kind) => kind switch
    {
        ContentKind.ResourcePack => "resourcepacks",
        ContentKind.Shader => "shaderpacks",
        _ => "mods"
    };

    /// <summary>下载目标目录：当前整合包运行目录下的 mods / resourcepacks / shaderpacks。</summary>
    public static string TargetDir(AppConfig cfg, string versionId, ContentKind kind) =>
        Path.Combine(cfg.RunDir(versionId), FolderName(kind));
}

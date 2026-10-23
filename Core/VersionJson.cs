using System.Text.Json;
using System.Text.RegularExpressions;

namespace OverrideMirageLauncher.Core;

/// <summary>
/// 版本 JSON 的解析辅助：规则判定、参数求值、占位符替换。
/// </summary>
public static partial class VersionJson
{
    [GeneratedRegex(@"\$\{([^}]+)\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();

    /// <summary>
    /// 判定带 rules 的条目是否适用于当前平台（Windows x64）。
    /// </summary>
    public static bool RulesAllow(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return true;
        if (!element.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            return true;

        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (!RuleMatches(rule)) continue;
            var action = rule.TryGetProperty("action", out var a) ? a.GetString() : null;
            allowed = string.Equals(action, "allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    private static bool RuleMatches(JsonElement rule)
    {
        // features 表示启动器能力开关（如 quickPlay、demo）。本启动器一个都不启用，
        // 因此带 features 的规则一律不匹配；否则 quickPlay 的几个互斥参数会被同时传入。
        if (rule.TryGetProperty("features", out var features) &&
            features.ValueKind == JsonValueKind.Object)
            return false;

        if (!rule.TryGetProperty("os", out var os) || os.ValueKind != JsonValueKind.Object)
            return true;

        if (os.TryGetProperty("name", out var nameEl) && nameEl.GetString() is { } name)
        {
            if (!string.Equals(name, "windows", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (os.TryGetProperty("arch", out var archEl) && archEl.GetString() is { } arch)
        {
            var is64 = Environment.Is64BitProcess;
            if (string.Equals(arch, "x86", StringComparison.OrdinalIgnoreCase) && is64) return false;
            if (string.Equals(arch, "x64", StringComparison.OrdinalIgnoreCase) && !is64) return false;
            if (string.Equals(arch, "arm64", StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    /// <summary>
    /// 把 arguments 数组求值成字符串列表，并替换占位符。
    /// </summary>
    public static List<string> EvaluateArguments(JsonElement array, IReadOnlyDictionary<string, string> vars)
    {
        var result = new List<string>();
        if (array.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in array.EnumerateArray())
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.String:
                    result.Add(Substitute(item.GetString() ?? "", vars));
                    break;

                case JsonValueKind.Object:
                    if (!RulesAllow(item)) break;
                    if (!item.TryGetProperty("value", out var value)) break;

                    if (value.ValueKind == JsonValueKind.String)
                    {
                        result.Add(Substitute(value.GetString() ?? "", vars));
                    }
                    else if (value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in value.EnumerateArray())
                            if (s.ValueKind == JsonValueKind.String)
                                result.Add(Substitute(s.GetString() ?? "", vars));
                    }
                    break;
            }
        }

        return result;
    }

    public static string Substitute(string text, IReadOnlyDictionary<string, string> vars)
    {
        return PlaceholderRegex().Replace(text, m =>
            vars.TryGetValue(m.Groups[1].Value, out var v) ? v : "");
    }

    /// <summary>
    /// 旧版本用 minecraftArguments 字符串，按空格切分并替换占位符。
    /// </summary>
    public static List<string> EvaluateLegacyArguments(string raw, IReadOnlyDictionary<string, string> vars)
    {
        return raw.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                  .Select(part => Substitute(part, vars))
                  .ToList();
    }

    /// <summary>
    /// 读取版本 JSON 中需要的 Java 主版本（缺省按 8 处理）。
    /// </summary>
    public static int GetRequiredJavaMajor(JsonElement root)
    {
        if (root.TryGetProperty("javaVersion", out var jv) &&
            jv.TryGetProperty("majorVersion", out var m) &&
            m.TryGetInt32(out var major))
            return major;
        return 8;
    }

    public static string GetString(JsonElement root, string name, string fallback = "")
    {
        return root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? fallback
            : fallback;
    }
}

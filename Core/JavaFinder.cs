using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace OverrideMirageLauncher.Core;

public sealed record JavaInfo(string Path, int MajorVersion)
{
    public string Display => $"Java {MajorVersion} — {Path}";
}

/// <summary>
/// 查找本机可用的 Java。优先取满足版本要求的最高版本。
/// </summary>
public static class JavaFinder
{
    public static List<JavaInfo> Discover(string gameDir)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddDirIfValid(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
            var javaw = Path.Combine(dir, "javaw.exe");
            var java = Path.Combine(dir, "java.exe");
            if (File.Exists(javaw)) candidates.Add(javaw);
            else if (File.Exists(java)) candidates.Add(java);
        }

        // 1. JAVA_HOME
        AddDirIfValid(Path.Combine(Environment.GetEnvironmentVariable("JAVA_HOME") ?? "", "bin"));

        // 2. PATH 上的 java
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            AddDirIfValid(dir);

        // 3. 常见安装位置
        var roots = new[]
        {
            @"C:\Program Files\Java", @"C:\Program Files (x86)\Java",
            @"C:\Program Files\Eclipse Adoptium", @"C:\Program Files\Microsoft",
            @"C:\Program Files\Zulu", @"C:\Program Files\BellSoft",
            @"C:\Program Files\Amazon Corretto",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Eclipse Adoptium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft"),
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var sub in SafeDirs(root))
                AddDirIfValid(Path.Combine(sub, "bin"));
            AddDirIfValid(Path.Combine(root, "bin"));
        }

        // 4. 游戏目录下的 runtime（PCL / 官方启动器自带运行时）
        var runtimeRoots = new[]
        {
            Paths.RuntimeDir(gameDir),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime")
        };
        foreach (var root in runtimeRoots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var javaw in SafeFiles(root, "javaw.exe", 6))
                candidates.Add(javaw);
        }

        var result = new List<JavaInfo>();
        foreach (var path in candidates)
        {
            var major = GetMajorVersion(path);
            if (major > 0) result.Add(new JavaInfo(path, major));
        }

        return result.OrderByDescending(j => j.MajorVersion).ToList();
    }

    /// <summary>
    /// 读取 java -version 输出的主版本号。失败返回 0。
    /// </summary>
    public static int GetMajorVersion(string javaPath)
    {
        try
        {
            var psi = new ProcessStartInfo(javaPath, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return 0;

            var text = proc.StandardError.ReadToEnd() + proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(5000))
                try { proc.Kill(true); } catch { }

            var match = Regex.Match(text, "version \"(\\d+)(?:\\.(\\d+))?");
            if (!match.Success) return 0;

            var first = int.Parse(match.Groups[1].Value);
            if (first == 1 && match.Groups[2].Success)
                return int.Parse(match.Groups[2].Value); // 1.8 -> 8

            return first;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 按版本需求挑选 Java：满足最低要求的里面取最小可用版本；都不满足时取最高版本并返回 false。
    /// </summary>
    public static JavaInfo? Pick(List<JavaInfo> available, int requiredMajor, out bool satisfied)
    {
        satisfied = false;
        if (available.Count == 0) return null;

        var ok = available.Where(j => j.MajorVersion >= requiredMajor)
                          .OrderBy(j => j.MajorVersion)
                          .FirstOrDefault();
        if (ok is not null)
        {
            satisfied = true;
            return ok;
        }

        return available.OrderByDescending(j => j.MajorVersion).First();
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeFiles(string root, string pattern, int maxDepth)
    {
        var results = new List<string>();
        try
        {
            var queue = new Queue<(string dir, int depth)>();
            queue.Enqueue((root, 0));
            while (queue.Count > 0)
            {
                var (dir, depth) = queue.Dequeue();
                if (depth > maxDepth) continue;
                foreach (var file in Directory.EnumerateFiles(dir, pattern))
                    results.Add(file);
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    queue.Enqueue((sub, depth + 1));
            }
        }
        catch { /* 忽略权限问题 */ }
        return results;
    }
}

using System.IO;

namespace OverrideMirageLauncher.Core;

public static class LogBus
{
    private static readonly object Gate = new();
    private static string _file = "";

    public static event Action<string>? OnLine;

    private static string FilePath
    {
        get
        {
            if (_file.Length > 0) return _file;
            var dir = Path.Combine(Paths.BaseDir, "logs");
            Directory.CreateDirectory(dir);
            _file = Path.Combine(dir, $"launcher-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            return _file;
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERR ", message);

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
        lock (Gate)
        {
            try
            {
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch
            {

            }
        }

        OnLine?.Invoke(line);
    }
}

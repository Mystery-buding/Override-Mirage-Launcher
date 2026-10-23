using System.Windows;
using OverrideMirageLauncher.Core;

namespace OverrideMirageLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 导出自带的方块图标，方便用户拿去二次编辑（画图 / PS / Aseprite）
        var exportIndex = IndexOf(e.Args, "--exporticons");
        if (exportIndex >= 0)
        {
            var dir = exportIndex + 1 < e.Args.Length && !e.Args[exportIndex + 1].StartsWith("--")
                ? e.Args[exportIndex + 1]
                : BlockArt.OverrideDir;

            try
            {
                BlockArt.ExportAll(dir);
                LogBus.Info("方块图标已导出：" + dir);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogBus.Error("导出方块图标失败：" + ex);
                Environment.Exit(1);
            }

            return;
        }

        // 命令行模式下先用线程池跑，避免 UI 同步上下文与阻塞等待互相死锁；
        // 结束后直接 Environment.Exit，跳过 WPF 的关闭协商。
        if (e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            var code = Task.Run(() => SelfTest.RunAsync()).GetAwaiter().GetResult();
            Environment.Exit(code);
            return;
        }

        var installIndex = IndexOf(e.Args, "--install");
        if (installIndex >= 0 && installIndex + 1 < e.Args.Length)
        {
            var version = e.Args[installIndex + 1];
            var code = Task.Run(() => CliRunner.InstallAsync(version)).GetAwaiter().GetResult();
            Environment.Exit(code);
            return;
        }

        var loaderIndex = IndexOf(e.Args, "--installloader");
        if (loaderIndex >= 0 && loaderIndex + 3 < e.Args.Length)
        {
            var kind = e.Args[loaderIndex + 1];
            var mc = e.Args[loaderIndex + 2];
            var loader = e.Args[loaderIndex + 3];
            var code = Task.Run(() => CliRunner.InstallLoaderAsync(kind, mc, loader)).GetAwaiter().GetResult();
            Environment.Exit(code);
            return;
        }

        // 正版登录诊断：一步步打印 Xbox → XSTS → Minecraft → 档案的结果，写到 mslogin.log
        if (e.Args.Contains("--mslogin", StringComparer.OrdinalIgnoreCase))
        {
            var code = Task.Run(() => CliRunner.MicrosoftLoginTestAsync()).GetAwaiter().GetResult();
            Environment.Exit(code);
            return;
        }

        // 把启动器给别人之前清掉本机账号（账号只在 config.json 里，不写进程序）
        if (e.Args.Contains("--resetaccount", StringComparer.OrdinalIgnoreCase))
        {
            Environment.Exit(CliRunner.ResetAccount());
            return;
        }

        var comboIndex = IndexOf(e.Args, "--combo");
        if (comboIndex >= 0 && comboIndex + 1 < e.Args.Length)
        {
            var rest = e.Args[(comboIndex + 1)..];
            var code = Task.Run(() => CliRunner.ComboAsync(rest)).GetAwaiter().GetResult();
            Environment.Exit(code);
            return;
        }

        var smokeIndex = IndexOf(e.Args, "--smokelaunch");
        if (smokeIndex >= 0 && smokeIndex + 1 < e.Args.Length)
        {
            var version = e.Args[smokeIndex + 1];
            var player = smokeIndex + 2 < e.Args.Length ? e.Args[smokeIndex + 2] : null;
            var code = Task.Run(() => CliRunner.SmokeLaunch(version, player)).GetAwaiter().GetResult();
            Environment.Exit(code);
            return;
        }

        if (e.Args.Contains("--uitest", StringComparer.OrdinalIgnoreCase))
        {
            var window = new MainWindow();
            window.Show();
            _ = window.UiSelfTestAsync().ContinueWith(task =>
            {
                Dispatcher.Invoke(() => Environment.Exit(task.Status == TaskStatus.RanToCompletion ? 0 : 1));
            });
            return;
        }

        new MainWindow().Show();
    }

    private static int IndexOf(string[] args, string value)
    {
        for (var i = 0; i < args.Length; i++)
            if (args[i].Equals(value, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using OverrideMirageLauncher.Core;

namespace OverrideMirageLauncher;

public partial class MainWindow : Window
{
    private const double NavItemHeight = 44;

    private readonly AppConfig _cfg;
    private List<JavaInfo> _javas = new();
    private List<ModpackEntry> _modpacks = new();
    private ModpackEntry? _currentPack;
    private bool _busy;
    private bool _syncingAuth;
    private bool _downloadLoaded;
    private bool _downloadLoading;
    private bool _syncingSource;
    private List<VersionSummary> _allVersions = new();
    private string _latestReleaseId = "";
    private string _latestSnapshotId = "";
    private string _currentCategory = "";
    private LoaderKind? _dlLoaderKind;
    private VersionSummary? _selectedDlVersion;

    /// <summary>官方清单里标记为快照、但实际是愚人节整活的版本。</summary>
    private static readonly HashSet<string> AprilFoolsIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "15w14a", "1.RV-Pre1", "3D Shareware v1.34", "20w14infinite",
        "22w13oneblockatatime", "23w13a_or_b", "24w14potato", "25w14craftmine"
    };
    private int _currentPage = -1;
    private Task _initialLoad = Task.CompletedTask;
    private CancellationTokenSource? _msLoginCts;
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer;
    private readonly System.Windows.Threading.DispatcherTimer _nameSaveTimer;

    public MainWindow()
    {
        InitializeComponent();
        _cfg = AppConfig.Load();
        DownloadSource.Configure(_cfg.UseMirror);

        SrcMinecraft.IsChecked = true;

        _statusTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            if (!_busy) StatusStrip.Visibility = Visibility.Collapsed;
        };

        // 用户名改动后延迟落盘：既不会每个按键都写一次，也能保证关窗前一定保存
        _nameSaveTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _nameSaveTimer.Tick += (_, _) =>
        {
            _nameSaveTimer.Stop();
            try { SavePlayerName(); }
            catch (Exception ex) { LogBus.Error("保存用户名失败：" + ex.Message); }
        };

        Loaded += MainWindow_Loaded;
        Closing += (_, _) =>
        {
            _nameSaveTimer.Stop();
            try { SaveConfigFromUi(); }
            catch (Exception ex) { LogBus.Error("退出时保存配置失败：" + ex.Message); }
        };
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            _msLoginCts?.Cancel();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 先建立初始页，再勾选导航项，避免 Checked 在控件树构建完成前触发
        ShowPage(0, animate: false);
        NavLaunch.IsChecked = true;

        TxtName.Text = _cfg.PlayerName;
        TxtGameDir.Text = _cfg.GameDir;
        TxtMemory.Text = _cfg.MemoryMb.ToString();
        TxtClientId.Text = _cfg.MsClientId;
        DlNameIcon.Child = ModpackIcons.Create(new ModpackEntry("", null, null), 34);
        BuildCategorySections();

        _syncingSource = true;
        CmbSource.SelectedIndex = _cfg.UseMirror ? 1 : 0;
        _syncingSource = false;

        _syncingSource = true;
        ChkIsolate.IsChecked = _cfg.IsolateInstances;
        _syncingSource = false;
        UpdateIsolateHint();

        InitContentFilters();
        TxtCfApiKey.Text = _cfg.CfApiKey;

        ApplyAuthMode();
        UpdateMicrosoftUi();

        LogBus.Info($"Override Mirage Launcher 启动，程序目录 {Paths.BaseDir}");
        LogPaths();
        LogBus.Info($"登录方式 {(_cfg.AuthMode == "microsoft" ? "正版" : "离线")}");

        _initialLoad = LoadEnvironmentAsync();
        await _initialLoad;

        await TrySilentRefreshAsync();
    }

    private void ChkIsolate_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingSource) return;

        _cfg.IsolateInstances = ChkIsolate.IsChecked == true;
        _cfg.Save();
        UpdateIsolateHint();

        LogBus.Info(_cfg.IsolateInstances ? "已开启版本隔离" : "已关闭版本隔离，所有整合包共用游戏目录");
    }

    private void UpdateIsolateHint()
    {
        var id = _currentPack?.Id ?? "";
        var runDir = _cfg.IsolateInstances && id.Length > 0
            ? Paths.InstanceDir(_cfg.GameDir, id)
            : _cfg.GameDir;

        TxtIsolateHint.Text = _cfg.IsolateInstances
            ? $"当前整合包的目录：{runDir}\n每个整合包各自一套 mods / saves / config，互不干扰；版本、依赖库、资源仍然共用，不会重复下载。"
            : "所有整合包共用游戏目录，mods 与存档混在一起，换版本时容易冲突。";
    }

    /// <summary>启动时把实际用到的目录一次性打进日志，方便确认东西都装在哪。</summary>
    private void LogPaths()
    {
        var gameDir = _cfg.GameDir;

        LogBus.Info("—— 目录一览 ——");
        LogBus.Info($"  程序目录   {Paths.BaseDir}");
        LogBus.Info($"  配置文件   {Path.Combine(Paths.BaseDir, "config.json")}");
        LogBus.Info($"  日志目录   {Path.Combine(Paths.BaseDir, "logs")}");
        LogBus.Info($"  游戏目录   {gameDir}");
        LogBus.Info($"  版本目录   {Paths.VersionsDir(gameDir)}");
        LogBus.Info($"  依赖库     {Paths.LibrariesDir(gameDir)}");
        LogBus.Info($"  资源文件   {Paths.AssetsDir(gameDir)}");

        var runDir = _cfg.RunDir(_currentPack?.Id ?? "");
        LogBus.Info($"  运行目录   {runDir}{(_cfg.IsolateInstances ? "（版本隔离）" : "（共用）")}");
        LogBus.Info($"  模组目录   {Path.Combine(runDir, "mods")}");
        LogBus.Info($"  存档目录   {Path.Combine(runDir, "saves")}");
        LogBus.Info("————————————");
    }

    private async Task LoadEnvironmentAsync()
    {
        await RefreshJavaAsync();
        LoadLocalVersions();
    }

    // ---------------- 页面切换 ----------------

    /// <summary>
    /// 界面自检：程序化切换页面，校验可见性、透明度与指示条位置，结果写入 uitest.log。
    /// </summary>
    public async Task UiSelfTestAsync()
    {
        var report = new List<string>();
        FrameworkElement[] pages = { PageLaunch, PageSettingsScroll };
        FrameworkElement[] contents = { PageLaunch, PageSettings };
        var names = new[] { "主页", "设置" };

        try
        {
            report.Add("=== 页面切换自检 ===");

            // 等首屏数据（Java / 版本清单）就位再切页；超时也不阻断自检
            await Task.WhenAny(_initialLoad, Task.Delay(8000));

            foreach (var index in new[] { 0, 1, 0 })
            {
                ShowPage(index);
                await Task.Delay(420);

                var visibility = string.Join(",",
                    pages.Select((p, i) => $"{names[i]}={(p.Visibility == Visibility.Visible ? "显示" : "隐藏")}"));
                var opacity = string.Join(",",
                    pages.Select((p, i) => $"{names[i]}={p.Opacity:0.00}"));
                var indicator = NavIndicatorTransform.Y;

                report.Add($"切到 [{names[index]}] → {visibility} | 透明度 {opacity} | 指示条 Y={indicator:0}");

                if (pages[index].Visibility != Visibility.Visible)
                    report.Add("  !! 目标页未显示");
                if (Math.Abs(pages[index].Opacity - 1) > 0.01)
                    report.Add($"  !! 目标页透明度异常 {pages[index].Opacity:0.00}");
                var expectedY = NavIndexOf(index) * NavItemHeight;
                if (Math.Abs(indicator - expectedY) > 0.6)
                    report.Add($"  !! 指示条位置异常，期望 {expectedY}");

                if (contents[index].ActualHeight > PageHost.ActualHeight + 1)
                {
                    report.Add(HasScrollableAncestor(contents[index])
                        ? $"  内容 {contents[index].ActualHeight:0} > 视区 {PageHost.ActualHeight:0}（已包 ScrollViewer，可滚动查看）"
                        : $"  !! 内容超出可视区且无法滚动：{contents[index].ActualHeight:0} > {PageHost.ActualHeight:0}");
                }
            }

            // 主页上的控件能不能真的点到（被覆盖层 / 滚动容器挡住的话这里会报「被挡」）
            ShowPage(0);
            await Task.Delay(420);
            foreach (var (label, el) in new (string, FrameworkElement)[]
                     { ("正版登录", SegMicrosoft), ("离线登录", SegOffline), ("用户名", TxtName) })
            {
                var desc = DescribeHitTest(el);
                report.Add($"主页命中测试 {label}：{desc}");
                if (desc.StartsWith("被挡", StringComparison.Ordinal))
                    report.Add($"  !! {label} 点不到，被别的元素挡住了");
            }

            // 下载是整窗覆盖层，不属于页面切换，单独校验
            OpenDownloadOverlay();
            await Task.Delay(280);
            var span = (int)DownloadOverlay.GetValue(Grid.ColumnSpanProperty);
            report.Add($"下载覆盖层 → {DownloadOverlay.Visibility}，跨越列数 {span}");
            if (DownloadOverlay.Visibility != Visibility.Visible)
                report.Add("  !! 下载覆盖层未显示");
            if (span != 2)
                report.Add("  !! 下载覆盖层没有覆盖左侧栏");

            // 等清单加载完，确认分类区域与「最新版」展开内容已生成
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (SectionRowCount("latest") == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(100);

            report.Add($"分类区域 {_sections.Count} 个，最新版 {SectionRowCount("latest")} 行");
            if (_sections.Count != 5)
                report.Add("  !! 分类区域数量异常");
            if (SectionRowCount("latest") == 0)
                report.Add("  !! 最新版区域为空");

            // 来源切换：模组 / 资源包 / 光影 走 Modrinth 搜索页
            SrcMods.IsChecked = true;
            await Task.Delay(160);
            report.Add($"切到「模组」→ 内容页 {DownloadContent.Visibility}，分类 {DownloadCategories.Visibility}");
            if (DownloadContent.Visibility != Visibility.Visible || DownloadCategories.Visibility != Visibility.Collapsed)
                report.Add("  !! 来源切换未生效");

            // 真搜一次，确认接口通、结果行能渲染出来
            TxtContentSearch.Text = "jei";
            _ = SearchContentAsync();
            var searchDeadline = DateTime.UtcNow.AddSeconds(25);
            while (ContentResults.Children.Count == 0 && DateTime.UtcNow < searchDeadline)
                await Task.Delay(200);
            report.Add($"模组搜索「{TxtContentSearch.Text}」→ {ContentResults.Children.Count} 行（{TxtContentEmpty.Text}）");
            if (ContentResults.Children.Count == 0)
                report.Add("  !! 模组搜索没有结果");

            // 真装一个模组：装完后行上的「下载中…」必须换回版本信息（曾经一直卡在「下载中…」）
            var firstRow = _contentRows.Values.FirstOrDefault();
            if (firstRow is not null && _currentPack is not null)
            {
                _ = ToggleContentVersionsAsync(firstRow.Project, firstRow);
                // 拉版本时列表里先放的是「正在获取版本列表…」文本，等真正渲染出版本行
                var versionDeadline = DateTime.UtcNow.AddSeconds(25);
                while (DateTime.UtcNow < versionDeadline && !firstRow.List.Children.OfType<Border>().Any())
                    await Task.Delay(200);

                var versionRow = firstRow.List.Children.OfType<Border>().FirstOrDefault();
                if (versionRow is null)
                {
                    report.Add("  !! 版本列表没有渲染出可点的行");
                }
                else
                {
                    versionRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    {
                        RoutedEvent = UIElement.MouseLeftButtonUpEvent
                    });

                    var installDeadline = DateTime.UtcNow.AddSeconds(120);
                    while (DateTime.UtcNow < installDeadline && _contentInstalling)
                        await Task.Delay(300);
                    await Task.Delay(400);

                    var metaText = (versionRow.Child as Grid)?.Children.OfType<TextBlock>().LastOrDefault()?.Text ?? "";
                    report.Add($"装完后的版本行：meta「{metaText}」/ 状态「{firstRow.State.Text}」");
                    if (metaText.Contains("下载中", StringComparison.Ordinal))
                        report.Add("  !! 装完后行上仍然显示「下载中…」");
                    if (!firstRow.State.Text.StartsWith("已安装", StringComparison.Ordinal))
                        report.Add("  !! 装完后状态没有变成「已安装 …」");

                    // 测试下载的文件不留在本机
                    const string prefix = "已安装到 ";
                    if (TxtStatus.Text.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        var installed = TxtStatus.Text[prefix.Length..].Trim();
                        try
                        {
                            if (File.Exists(installed))
                            {
                                File.Delete(installed);
                                report.Add($"已清理测试下载：{Path.GetFileName(installed)}");
                            }
                        }
                        catch
                        {
                            // 清理失败不影响自检结论
                        }
                    }
                }
            }

            SrcMinecraft.IsChecked = true;

            // 正式版安装器视图：四行加载器菜单应都建出来并开始查版本
            OpenInstaller("release");
            await Task.Delay(220);
            report.Add($"安装界面版本：{_selectedDlVersion?.Id ?? "无"}，加载器菜单 {_loaderMenus.Count} 个");
            report.Add($"安装摘要：{TxtDlSummary.Text}");
            if (_selectedDlVersion is null)
                report.Add("  !! 安装界面没有选中版本");
            if (_loaderMenus.Count != 4)
                report.Add("  !! 加载器菜单数量异常");
            if (TxtDlSummary.Text.Length == 0)
                report.Add("  !! 安装摘要为空");

            // 等 NeoForge 的版本拉回来，展开菜单选第一个，应自动命名并刷新摘要
            var neoUi = _loaderMenus[LoaderKind.NeoForge];
            var menuDeadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < menuDeadline && !neoUi.Loaded)
                await Task.Delay(200);

            report.Add($"NeoForge 菜单：{neoUi.Versions.Count} 个版本，提示「{neoUi.Hint.Text}」");
            if (neoUi.Versions.Count > 0)
            {
                ToggleLoaderMenu(neoUi);
                await Task.Delay(120);
                SelectLoaderVersion(neoUi, neoUi.Versions[0]);
                report.Add($"选 {neoUi.Versions[0]} → 自动命名「{TxtDlName.Text}」，摘要：{TxtDlSummary.Text}");
                if (_dlLoaderKind != LoaderKind.NeoForge || _dlLoaderVersion != neoUi.Versions[0])
                    report.Add("  !! 加载器版本选择没有生效");
                if (TxtDlName.Text.Length == 0)
                    report.Add("  !! 选了加载器却没有自动生成名称");
                if (!TxtDlSummary.Text.Contains("1 个整合包"))
                    report.Add("  !! 摘要没有说明「本体 + 加载器 = 一个整合包」");
            }

            BtnDlBack_Click(this, new RoutedEventArgs());

            CloseDownloadOverlay();
            await Task.Delay(340);
            if (DownloadOverlay.Visibility != Visibility.Collapsed)
                report.Add("  !! 下载覆盖层未关闭");

            if (NavDownload.IsChecked == true)
                report.Add("  !! 关闭后「下载」导航仍处于选中状态");

            // 整合包选择界面：整行展示，每行右侧一个删除按钮
            BuildPickerCards();
            PickerOverlay.Visibility = Visibility.Visible;
            await Task.Delay(220);

            var rowCount = PickerPanel.Children.Count;
            report.Add($"整合包界面：{rowCount} 行（本地 {_modpacks.Count} 个）");
            if (_modpacks.Count > 0 && rowCount != _modpacks.Count)
                report.Add("  !! 整合包行数与本地数量不符");
            if (_modpacks.Count == 0 && PickerEmpty.Visibility != Visibility.Visible)
                report.Add("  !! 空列表没有显示占位页");

            var rowButtons = PickerPanel.Children.OfType<Border>()
                .Select(CountVisualChildren<Button>)
                .ToList();
            report.Add($"每行操作按钮：{string.Join(",", rowButtons)}（应为 2：打开文件夹 + 删除）");
            if (_modpacks.Count > 0 && rowButtons.Any(c => c != 2))
                report.Add("  !! 行内按钮数量不是 2");

            PickerOverlay.Visibility = Visibility.Collapsed;

            report.Add("自检完成");
        }
        catch (Exception ex)
        {
            report.Add("自检异常：" + ex);
        }

        try
        {
            File.WriteAllText(Path.Combine(Paths.BaseDir, "uitest.log"), string.Join(Environment.NewLine, report));
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>对控件中心做一次命中测试，看最上层是不是它自己（用来发现「看得见但点不到」）。</summary>
    private string DescribeHitTest(FrameworkElement element)
    {
        element.UpdateLayout();
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return "控件尺寸为 0";

        var screen = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        var hit = InputHitTest(PointFromScreen(screen)) as DependencyObject;

        for (var d = hit; d is not null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, element))
                return $"命中（{hit?.GetType().Name}）";

        var name = "";
        for (var d = hit; d is not null && name.Length == 0; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement fe && fe.Name.Length > 0) name = fe.Name;

        return $"被挡（最上层 {hit?.GetType().Name}{(name.Length > 0 ? "/" + name : "")}）";
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string raw }) return;

        // 下载不是普通页面：整窗覆盖层，不参与页切换与指示条
        if (raw == "-1")
        {
            OpenDownloadOverlay();
            return;
        }

        if (int.TryParse(raw, out var navIndex)) ShowPage(PageIndexOf(navIndex));
    }

    /// <summary>页面下标 → 导航项下标（下载占了一格，但不属于页面）。</summary>
    private static int NavIndexOf(int pageIndex) => pageIndex switch
    {
        0 => 0,
        1 => 2,  // 设置
        _ => pageIndex
    };

    /// <summary>导航项下标 → 页面下标，与 <see cref="NavIndexOf"/> 互为逆运算。</summary>
    private static int PageIndexOf(int navIndex) => navIndex switch
    {
        0 => 0,
        2 => 1,  // 设置
        _ => -1
    };

    private void ShowPage(int index, bool animate = true)
    {
        // 宿主 = 页面本身（设置页是那层 ScrollViewer，折叠它才能彻底让位给主页）
        FrameworkElement[] pages = { PageLaunch, PageSettingsScroll };
        if (index < 0 || index >= pages.Length) return;
        if (_currentPage == index && animate) return;

        var target = pages[index];
        var previousIndex = _currentPage;
        var direction = index >= previousIndex ? 1 : -1;

        // 旧页淡出后收起
        if (previousIndex >= 0 && previousIndex != index)
        {
            var previous = pages[previousIndex];
            if (animate)
            {
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(110));
                fadeOut.Completed += (_, _) =>
                {
                    previous.Visibility = Visibility.Collapsed;
                    previous.BeginAnimation(OpacityProperty, null); // 解除 HoldEnd，避免残留 0 透明度
                    previous.Opacity = 1;
                };
                previous.BeginAnimation(OpacityProperty, fadeOut);
            }
            else
            {
                previous.BeginAnimation(OpacityProperty, null);
                previous.Visibility = Visibility.Collapsed;
                previous.Opacity = 1;
            }
        }

        target.Visibility = Visibility.Visible;

        // 新页：横向滑入 + 淡入，方向跟随导航方向
        if (target.RenderTransform is System.Windows.Media.TranslateTransform transform)
        {
            if (animate)
            {
                var slide = new DoubleAnimation(direction * 28, 0, TimeSpan.FromMilliseconds(260))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                transform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slide);
            }
            else
            {
                transform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
                transform.X = 0;
            }
        }

        if (animate)
        {
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(210));
            target.BeginAnimation(OpacityProperty, fadeIn);
        }
        else
        {
            target.BeginAnimation(OpacityProperty, null);
            target.Opacity = 1;
        }

        AnimateNavIndicator(NavIndexOf(index), animate);
        _currentPage = index;
    }

    private void AnimateNavIndicator(int index, bool animate)
    {
        var target = index * NavItemHeight;

        if (animate)
        {
            var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            NavIndicatorTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, animation);
        }
        else
        {
            NavIndicatorTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            NavIndicatorTransform.Y = target;
        }
    }

    // ---------------- 状态条 ----------------

    /// <summary>
    /// 状态条平时收起，只在有操作时短暂出现；忙碌期间常驻，结束后 4 秒自动隐藏。
    /// </summary>
    private void SetStatus(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<string>(SetStatus), text);
            return;
        }

        TxtStatus.Text = text;
        StatusStrip.Visibility = Visibility.Visible;

        _statusTimer.Stop();
        if (!_busy) _statusTimer.Start();
    }

    // ---------------- 初始化 ----------------

    private async Task RefreshJavaAsync()
    {
        SetStatus("正在查找本机 Java…");
        try
        {
            _javas = await Task.Run(() => JavaFinder.Discover(_cfg.GameDir));

            CmbJava.ItemsSource = null;
            CmbJava.ItemsSource = _javas;
            CmbJava.DisplayMemberPath = nameof(JavaInfo.Display);

            if (_javas.Count > 0)
            {
                var preferred = _javas.FirstOrDefault(j => j.Path == _cfg.JavaPath) ?? _javas[0];
                CmbJava.SelectedItem = preferred;
                LogBus.Info($"发现 {_javas.Count} 个 Java，默认使用 Java {preferred.MajorVersion}");
            }
            else
            {
                LogBus.Warn("没有找到可用的 Java，请手动指定 javaw.exe 的路径。");
                SetStatus("未找到 Java，请到「设置」里手动指定");
            }
        }
        catch (Exception ex)
        {
            LogBus.Error("查找 Java 失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 整合包列表只读本地 versions 目录：有同名 json 的才算装好。
    /// 官方版本不再混进列表，下载统一在「下载」页做。
    /// </summary>
    private void LoadLocalVersions()
    {
        try
        {
            _modpacks = ModpackCatalog.Scan(_cfg.GameDir);

            _currentPack = _modpacks.FirstOrDefault(p => p.Id == _cfg.VersionId) ?? _modpacks.FirstOrDefault();
            if (_currentPack is not null && _currentPack.Id != _cfg.VersionId)
            {
                _cfg.VersionId = _currentPack.Id;
                _cfg.Save();
            }

            UpdatePickerButton();
            if (PickerOverlay.Visibility == Visibility.Visible) BuildPickerCards();

            LogBus.Info($"本地整合包 {_modpacks.Count} 个：{string.Join(", ", _modpacks.Select(p => p.Id))}");
            SetStatus(_modpacks.Count > 0
                ? $"本地整合包 {_modpacks.Count} 个"
                : "本地还没有整合包，去「下载」页装一个");
        }
        catch (Exception ex)
        {
            LogBus.Error("扫描本地整合包失败：" + ex.Message);
            SetStatus("扫描本地整合包失败：" + ex.Message);
        }
    }

    // ---------------- 整合包选择 ----------------

    private void UpdatePickerButton()
    {
        PickerIconHost.Child = ModpackIcons.Create(_currentPack, 22);
        TxtPickerName.Text = _currentPack?.Id ?? "选择整合包";

        BtnModpackPicker.ToolTip = _currentPack is null
            ? "选择整合包"
            : _currentPack.Loader is { } loader
                ? $"{_currentPack.Id}（{LoaderService.DisplayName(loader)}）"
                : $"{_currentPack.Id}（原版）";
    }

    private void BtnModpackPicker_Click(object sender, RoutedEventArgs e)
    {
        BuildPickerCards();
        PickerOverlay.Visibility = Visibility.Visible;
    }

    private void BtnClosePicker_Click(object sender, RoutedEventArgs e) => PickerOverlay.Visibility = Visibility.Collapsed;

    private void BtnGoDownload_Click(object sender, RoutedEventArgs e)
    {
        PickerOverlay.Visibility = Visibility.Collapsed;
        NavDownload.IsChecked = true;
    }

    private void BuildPickerCards()
    {
        PickerPanel.Children.Clear();

        var empty = _modpacks.Count == 0;
        PickerEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        PickerScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (empty) return;

        foreach (var pack in _modpacks)
            PickerPanel.Children.Add(BuildPackRow(pack, pack.Id == _currentPack?.Id));
    }

    private FrameworkElement BuildPackRow(ModpackEntry pack, bool selected)
    {
        var name = new TextBlock
        {
            Text = pack.Id,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x21)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };

        var loaderName = pack.Loader is { } kind ? LoaderService.DisplayName(kind) : "原版";
        var loader = new TextBlock
        {
            Text = selected ? $"{loaderName} · 使用中" : loaderName,
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = new SolidColorBrush(selected
                ? Color.FromRgb(0x1F, 0x5C, 0x3F)
                : Color.FromRgb(0x8A, 0x92, 0xA0))
        };

        var texts = new StackPanel
        {
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        texts.Children.Add(name);
        texts.Children.Add(loader);

        var open = new Button
        {
            Content = "打开文件夹",
            Style = (Style)FindResource("RowButton"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = $"在资源管理器里打开「{_cfg.RunDir(pack.Id)}」（mods / 存档 / 配置都在这里）"
        };
        open.Click += (_, _) => OpenPackFolder(pack);

        var del = new Button
        {
            Content = "删除",
            Style = (Style)FindResource("DangerButton"),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"删除整合包「{pack.Id}」"
        };
        del.Click += (_, _) => DeletePack(pack);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        actions.Children.Add(open);
        actions.Children.Add(del);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = ModpackIcons.Create(pack, 40);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        var row = new Border
        {
            Height = 62,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(selected ? 2 : 1),
            Background = new SolidColorBrush(selected
                ? Color.FromRgb(0xF1, 0xF8, 0xF4)
                : Colors.White),
            BorderBrush = new SolidColorBrush(selected
                ? Color.FromRgb(0x2F, 0x6F, 0x4F)
                : Color.FromRgb(0xE4, 0xE8, 0xEE)),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = grid,
            ToolTip = pack.Id
        };

        row.MouseEnter += (_, _) =>
        {
            if (selected) return;
            row.BorderBrush = new SolidColorBrush(Color.FromRgb(0x9C, 0xBC, 0xA8));
        };
        row.MouseLeave += (_, _) =>
        {
            if (selected) return;
            row.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE8, 0xEE));
        };
        row.MouseLeftButtonUp += (_, a) =>
        {
            // 点在右侧操作按钮上时不触发选中
            if (IsVisualDescendantOf(a.OriginalSource as DependencyObject, actions)) return;
            SelectPack(pack.Id);
        };

        return row;
    }

    /// <summary>在资源管理器里打开该整合包的目录。</summary>
    private void OpenPackFolder(ModpackEntry pack)
    {
        // 版本隔离开启时 mods / 存档 / 配置都在独立目录里，那才是要打开的目录
        var dir = _cfg.IsolateInstances
            ? _cfg.RunDir(pack.Id)
            : Path.Combine(Paths.VersionsDir(_cfg.GameDir), pack.Id);

        try
        {
            // 独立目录还没建过（装完没启动）就先建出来，方便直接往里放 mod
            if (_cfg.IsolateInstances && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!Directory.Exists(dir))
            {
                MessageBox.Show(this, $"找不到目录：{dir}", "Override Mirage",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
            SetStatus($"已打开 {pack.Id} 的目录");
        }
        catch (Exception ex)
        {
            LogBus.Error($"打开目录失败：{ex.Message}");
            MessageBox.Show(this, $"打开目录失败：{ex.Message}", "Override Mirage",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>找出所有 inheritsFrom 指向该版本的整合包（模组加载器版本依赖原版）。</summary>
    private List<string> FindInheritedPacks(string parentId)
    {
        var result = new List<string>();
        var versionsDir = Paths.VersionsDir(_cfg.GameDir);
        if (!Directory.Exists(versionsDir)) return result;

        foreach (var dir in Directory.GetDirectories(versionsDir))
        {
            var id = Path.GetFileName(dir);
            if (string.Equals(id, parentId, StringComparison.OrdinalIgnoreCase)) continue;

            var json = Path.Combine(dir, id + ".json");
            if (!File.Exists(json)) continue;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                if (doc.RootElement.TryGetProperty("inheritsFrom", out var parent) &&
                    string.Equals(parent.GetString(), parentId, StringComparison.OrdinalIgnoreCase))
                    result.Add(id);
            }
            catch
            {
                // 单个文件读不了就跳过
            }
        }

        return result;
    }

    /// <summary>在视觉树里找第一个指定类型的子元素。</summary>
    private static T? FindVisualChild<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T typed) return typed;
            if (FindVisualChild<T>(child) is { } deeper) return deeper;
        }

        return null;
    }

    /// <summary>统计视觉树里指定类型的子元素个数。</summary>
    private static int CountVisualChildren<T>(DependencyObject node) where T : DependencyObject
    {
        var count = 0;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T) count++;
            count += CountVisualChildren<T>(child);
        }

        return count;
    }

    /// <summary>判断某个元素是否在 ScrollViewer 里（内容超高时可以滚动查看）。</summary>
    private static bool HasScrollableAncestor(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is ScrollViewer) return true;
            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    /// <summary>判断事件源是否在指定元素内部（用于让删除按钮不冒泡到整行的点击事件）。</summary>
    private static bool IsVisualDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    /// <summary>
    /// 删除一个本地整合包：整个 versions/&lt;id&gt; 目录；
    /// 若该整合包还有独立目录 instances/&lt;id&gt;（mods / 存档 / 配置），会先问是否一起删。
    /// </summary>
    private void DeletePack(ModpackEntry pack)
    {
        var dir = Path.Combine(Paths.VersionsDir(_cfg.GameDir), pack.Id);
        var instanceDir = Paths.InstanceDir(_cfg.GameDir, pack.Id);
        var hasInstance = Directory.Exists(instanceDir) &&
                          Directory.EnumerateFileSystemEntries(instanceDir).Any();

        var message = $"确定要删除整合包「{pack.Id}」吗？\n\n" +
                      $"将删除 versions\\{pack.Id}\\ 整个目录（版本 JSON、客户端 jar、natives）。\n此操作不可撤销。";

        var children = FindInheritedPacks(pack.Id);
        if (children.Count > 0)
            message += $"\n\n注意：还有 {children.Count} 个整合包继承了它（{string.Join("、", children)}），" +
                       "删掉之后它们会缺少客户端 jar 而无法启动。";

        var deleteInstance = false;

        if (hasInstance)
        {
            message += $"\n\n它还有独立目录 instances\\{pack.Id}\\，里面是自己的 mods / 存档 / 配置：\n" +
                       "「是」＝连同存档一起删除；「否」＝只删版本，保留存档。";

            var answer = MessageBox.Show(message, "删除整合包",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);

            if (answer == MessageBoxResult.Cancel) return;
            deleteInstance = answer == MessageBoxResult.Yes;
        }
        else
        {
            var answer = MessageBox.Show(message, "删除整合包",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);

            if (answer != MessageBoxResult.OK) return;
        }

        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
            else
            {
                var json = dir + ".json";
                if (File.Exists(json)) File.Delete(json);
            }

            if (deleteInstance)
            {
                Directory.Delete(instanceDir, recursive: true);
                LogBus.Info($"已删除独立目录：{instanceDir}");
            }
        }
        catch (Exception ex)
        {
            LogBus.Error($"删除整合包 {pack.Id} 失败：" + ex.Message);
            MessageBox.Show($"删除失败：{ex.Message}\n\n如果游戏正在运行，请先关闭再试。", "删除整合包",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LogBus.Info($"已删除整合包：{pack.Id}");

        if (string.Equals(_currentPack?.Id, pack.Id, StringComparison.Ordinal))
        {
            _currentPack = null;
            _cfg.VersionId = "";
            _cfg.Save();
        }

        LoadLocalVersions();
        SetStatus(hasInstance && !deleteInstance
            ? $"已删除整合包：{pack.Id}（存档保留在 instances\\{pack.Id}\\）"
            : $"已删除整合包：{pack.Id}");
    }

    /// <summary>切换当前整合包：写回配置、刷新按钮与「版本与模组」页，然后关掉选择界面。</summary>
    private void SelectPack(string id)
    {
        var pack = _modpacks.FirstOrDefault(p => p.Id == id);
        if (pack is null) return;

        _currentPack = pack;
        _cfg.VersionId = pack.Id;
        _cfg.Save();

        UpdatePickerButton();
        PickerOverlay.Visibility = Visibility.Collapsed;

        SetStatus($"已选择整合包：{pack.Id}");
    }

    /// <summary>下载页的官方版本清单，按需加载，只拉一次。</summary>
    private async Task LoadDownloadCatalogAsync(bool force)
    {
        if (_downloadLoading) return;
        if (_downloadLoaded && !force) return;

        _downloadLoading = true;
        try
        {
            SetStatus("正在获取官方版本清单…");
            var manifest = await VersionService.GetManifestAsync(force);

            _allVersions = manifest.Versions;
            _latestReleaseId = manifest.Latest?.Release ?? "";
            _latestSnapshotId = manifest.Latest?.Snapshot ?? "";
            _downloadLoaded = true;

            RefreshCategorySections();
            LogBus.Info($"分类区域已刷新：最新版 {SectionRowCount("latest")} 行，最新 {_latestReleaseId} / {_latestSnapshotId}");

            var releaseCount = _allVersions.Count(v => v.Type == "release");
            SetStatus($"官方清单 {_allVersions.Count} 项");
            LogBus.Info($"下载清单：正式版 {releaseCount}，预览版 {VersionsFor("snapshot").Count}，" +
                        $"远古版 {VersionsFor("legacy").Count}，愚人节版 {VersionsFor("april").Count}");

            // 已经在安装器视图里的话，切换分类后同步刷新版本下拉
            if (DownloadInstaller.Visibility == Visibility.Visible && _currentCategory.Length > 0)
                OpenInstaller(_currentCategory);
        }
        catch (Exception ex)
        {
            LogBus.Error("获取官方版本清单失败：" + ex.Message);
            SetStatus("版本清单获取失败，请检查网络后重试");
        }
        finally
        {
            _downloadLoading = false;
        }
    }

    // ---------------- 事件 ----------------

    // ---------------- 下载页 ----------------
    private void DownloadSource_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string key }) return;

        var minecraft = key == "minecraft";
        DownloadInstaller.Visibility = Visibility.Collapsed;
        ShowDownloadView(minecraft ? DownloadCategories : DownloadContent);

        if (minecraft)
        {
            _ = LoadDownloadCatalogAsync(false);
            return;
        }

        _contentKind = key switch
        {
            "packs" => ContentKind.ResourcePack,
            "shaders" => ContentKind.Shader,
            _ => ContentKind.Mod
        };

        PrepareContentSource();
    }

    /// <summary>下载页三个右侧视图之间的切换：只显示目标，带淡入横滑。</summary>
    private void ShowDownloadView(FrameworkElement view)
    {
        foreach (var other in new FrameworkElement[] { DownloadCategories, DownloadContent, DownloadInstaller })
        {
            if (ReferenceEquals(other, view)) continue;
            if (other.Visibility == Visibility.Visible) other.Visibility = Visibility.Collapsed;
        }

        view.Visibility = Visibility.Visible;
        UiAnim.SlideInX(view, 22);
    }

    // ---------------- 模组 / 资源包 / 光影（Modrinth + CurseForge） ----------------

    private ContentKind _contentKind = ContentKind.Mod;
    private ContentProvider _contentProvider = ContentProvider.Modrinth;
    private ContentKind? _contentLoadedKind;
    private ContentProvider? _contentLoadedProvider;
    private bool _contentBusy;
    private readonly Dictionary<string, ContentRowUi> _contentRows = new();

    /// <summary>游戏版本下拉的前两项：跟随整合包 / 全部版本，后面才是具体版本号。</summary>
    private const string FollowPack = "跟随整合包";
    private const string AnyVersion = "全部版本";

    private sealed class ContentRowUi
    {
        public ContentProject Project = null!;
        public Border Card = null!;
        public StackPanel List = null!;
        public TextBlock Chevron = null!;
        public TextBlock State = null!;
        public bool Expanded;
        public bool VersionsLoaded;
    }

    /// <summary>三个筛选下拉的初始化：版本、加载器、数据源。</summary>
    private void InitContentFilters()
    {
        _syncingSource = true;

        CmbContentMc.Items.Clear();
        CmbContentMc.Items.Add(FollowPack);
        CmbContentMc.Items.Add(AnyVersion);
        CmbContentMc.SelectedIndex = 0;

        CmbContentLoader.Items.Clear();
        CmbContentLoader.Items.Add(FollowPack);
        CmbContentLoader.Items.Add(AnyVersion);
        foreach (var name in new[] { "Fabric", "Forge", "NeoForge", "Quilt" })
            CmbContentLoader.Items.Add(name);
        CmbContentLoader.SelectedIndex = 0;

        CmbContentProvider.Items.Clear();
        CmbContentProvider.Items.Add("Modrinth");
        CmbContentProvider.Items.Add("CurseForge");
        CmbContentProvider.SelectedIndex = 0;

        _syncingSource = false;
    }

    /// <summary>把官方清单里的正式版填进「游戏版本」下拉（每次打开下载页刷新一次）。</summary>
    private void FillContentMcVersions()
    {
        var current = CmbContentMc.SelectedItem as string ?? FollowPack;

        _syncingSource = true;
        CmbContentMc.Items.Clear();
        CmbContentMc.Items.Add(FollowPack);
        CmbContentMc.Items.Add(AnyVersion);

        foreach (var version in VersionsFor("release"))
            CmbContentMc.Items.Add(version.Id);

        // 之前选的是具体版本就尽量保留，否则回到「跟随整合包」
        CmbContentMc.SelectedItem = CmbContentMc.Items.Contains(current) ? current : FollowPack;
        _syncingSource = false;
    }

    /// <summary>切到某个内容来源：改标题、刷新提示，必要时自动搜一次热门。</summary>
    private void PrepareContentSource()
    {
        TxtContentTitle.Text = ContentTypes.DisplayName(_contentKind);
        FillContentMcVersions();
        UpdateContentHint();

        // 资源包与光影没有加载器概念
        CmbContentLoader.IsEnabled = _contentKind == ContentKind.Mod;

        if (_contentLoadedKind == _contentKind && _contentLoadedProvider == _contentProvider) return;

        _contentRows.Clear();
        ContentResults.Children.Clear();
        TxtContentEmpty.Visibility = Visibility.Visible;
        TxtContentEmpty.Text = "正在获取热门内容…";
        _ = SearchContentAsync();
    }

    private void ContentFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSource) return;

        UpdateContentHint();
        _ = SearchContentAsync();
    }

    private void ContentProvider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSource) return;

        _contentProvider = CmbContentProvider.SelectedIndex == 1
            ? ContentProvider.CurseForge
            : ContentProvider.Modrinth;

        UpdateContentHint();
        _ = SearchContentAsync();
    }

    private void TxtContentSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) _ = SearchContentAsync();
    }

    private void BtnContentSearch_Click(object sender, RoutedEventArgs e) => _ = SearchContentAsync();

    private void BtnSaveCfKey_Click(object sender, RoutedEventArgs e)
    {
        _cfg.CfApiKey = TxtCfApiKey.Text.Trim();
        _cfg.Save();
        SetStatus(_cfg.CfApiKey.Length > 0 ? "CurseForge API Key 已保存" : "已清空 CurseForge API Key");
    }

    /// <summary>提示行：过滤条件、数据源与安装目标目录。</summary>
    private void UpdateContentHint()
    {
        var dir = ContentTypes.TargetDir(_cfg, _currentPack?.Id ?? "", _contentKind);
        TxtContentTarget.Text = $"安装到：{dir}";

        var (mc, loader) = ContentFilter();
        var provider = ContentTypes.ProviderName(_contentProvider);

        var parts = new List<string> { provider };
        parts.Add(mc is { Length: > 0 } m ? $"Minecraft {m}" : "不限游戏版本");
        if (_contentKind == ContentKind.Mod)
            parts.Add(loader is { Length: > 0 } l ? Upper(l) : "不限加载器");

        TxtContentHint.Text = string.Join(" · ", parts);

        if (_contentProvider == ContentProvider.CurseForge && _cfg.CfApiKey.Length == 0)
            TxtContentHint.Text += "（CurseForge 需要 API Key，在设置页填写后才能搜索）";
        else if (_currentPack is null && mc is null)
            TxtContentHint.Text += "。还没选整合包，会装到 .minecraft 的共享目录。";
    }

    private static string Upper(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>按两个下拉与当前整合包算出实际用于搜索的过滤条件。</summary>
    private (string? McVersion, string? Loader) ContentFilter()
    {
        var pack = _currentPack;

        // 游戏版本：跟随整合包 → 沿继承链找原版；全部版本 → 不过滤；否则用选中值
        var mcChoice = CmbContentMc.SelectedItem as string ?? FollowPack;
        var mc = mcChoice switch
        {
            FollowPack => pack is null ? null : McVersionOf(pack.Id),
            AnyVersion => null,
            _ => mcChoice
        };

        if (_contentKind != ContentKind.Mod) return (mc, null);

        var loaderChoice = CmbContentLoader.SelectedItem as string ?? FollowPack;
        var loader = loaderChoice switch
        {
            FollowPack => pack?.Loader switch
            {
                LoaderKind.Fabric => "fabric",
                LoaderKind.Quilt => "quilt",
                LoaderKind.Forge => "forge",
                LoaderKind.NeoForge => "neoforge",
                _ => null
            },
            AnyVersion => null,
            _ => loaderChoice.ToLowerInvariant()
        };

        return (mc, loader);
    }

    /// <summary>沿 inheritsFrom 链找到最顶层的原版 id，即 Minecraft 版本。</summary>
    private string? McVersionOf(string versionId)
    {
        var dir = Paths.VersionsDir(_cfg.GameDir);

        try
        {
            var id = versionId;
            for (var depth = 0; depth < 8 && !string.IsNullOrWhiteSpace(id); depth++)
            {
                var path = Path.Combine(dir, id, id + ".json");
                if (!File.Exists(path)) return null;

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("inheritsFrom", out var parent) &&
                    parent.GetString() is { Length: > 0 } next)
                {
                    id = next;
                    continue;
                }

                return id;
            }
        }
        catch (Exception ex)
        {
            LogBus.Error("读取 Minecraft 版本失败：" + ex.Message);
        }

        return null;
    }

    private async Task SearchContentAsync()
    {
        if (_contentBusy) return;
        _contentBusy = true;

        var name = ContentTypes.DisplayName(_contentKind);
        var provider = _contentProvider;
        BtnContentSearch.IsEnabled = false;
        _contentRows.Clear();
        ContentResults.Children.Clear();
        TxtContentEmpty.Visibility = Visibility.Visible;
        TxtContentEmpty.Text = $"正在从 {ContentTypes.ProviderName(provider)} 搜索{name}…";
        SetStatus($"正在搜索{name}…");

        try
        {
            var (mc, loader) = ContentFilter();
            var query = TxtContentSearch.Text.Trim();

            List<ContentProject> list;
            if (provider == ContentProvider.CurseForge)
            {
                if (_cfg.CfApiKey.Length == 0)
                {
                    TxtContentEmpty.Text = "CurseForge 需要 API Key：到设置页填一个（console.curseforge.com 免费申请），或者换回 Modrinth。";
                    SetStatus("CurseForge 需要 API Key");
                    return;
                }

                list = await CurseForgeService.SearchAsync(query, _contentKind, mc, loader, _cfg.CfApiKey);
            }
            else
            {
                list = await ModrinthService.SearchAsync(query, _contentKind, mc, loader);
            }

            _contentLoadedKind = _contentKind;
            _contentLoadedProvider = provider;

            if (list.Count == 0)
            {
                TxtContentEmpty.Text = "没有找到相关内容，换个关键词或放宽版本 / 加载器过滤试试。";
                SetStatus($"没有找到{name}");
                return;
            }

            TxtContentEmpty.Visibility = Visibility.Collapsed;

            for (var i = 0; i < list.Count; i++)
            {
                var row = BuildContentRow(list[i]);
                ContentResults.Children.Add(row);
                UiAnim.FadeIn(row, Math.Min(i, 12) * 24, 10);
            }

            SetStatus($"在 {ContentTypes.ProviderName(provider)} 找到 {list.Count} 个{name}");
        }
        catch (Exception ex)
        {
            LogBus.Error($"搜索{name}失败：" + ex.Message);
            TxtContentEmpty.Visibility = Visibility.Visible;
            TxtContentEmpty.Text = $"搜索失败：{ex.Message}";
            SetStatus($"搜索{name}失败：{ex.Message}");
        }
        finally
        {
            _contentBusy = false;
            BtnContentSearch.IsEnabled = true;
        }
    }

    /// <summary>搜索结果行：图标 + 标题 + 简介，点开是版本列表。</summary>
    private Border BuildContentRow(ContentProject project)
    {
        var chevron = new TextBlock
        {
            Text = "▸",
            FontSize = 13,
            Width = 16,
            Foreground = MenuHintFg,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5)
        };

        var title = new TextBlock
        {
            Text = project.Title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = MenuTitleFg,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };

        var desc = new TextBlock
        {
            Text = project.Description,
            FontSize = 12,
            Foreground = MenuHintFg,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0)
        };

        var texts = new StackPanel
        {
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        texts.Children.Add(title);
        texts.Children.Add(desc);

        var meta = new TextBlock
        {
            Text = $"{project.Author} · {FormatCount(project.Downloads)} 次下载",
            FontSize = 12,
            Foreground = MenuHintFg,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };

        // 右侧状态：默认提示「选择版本」，装完变成已安装的版本号
        var state = new TextBlock
        {
            Text = "选择版本",
            FontSize = 11,
            Foreground = MenuHintFg,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 3, 0, 0)
        };

        var rightStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        rightStack.Children.Add(meta);
        rightStack.Children.Add(state);

        var headerGrid = new Grid { Height = 56 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        headerGrid.Children.Add(BuildContentIcon(project, 40));
        Grid.SetColumn(texts, 1);
        headerGrid.Children.Add(texts);
        Grid.SetColumn(rightStack, 2);
        rightStack.Margin = new Thickness(10, 0, 0, 0);
        headerGrid.Children.Add(rightStack);
        Grid.SetColumn(chevron, 3);
        chevron.Margin = new Thickness(10, 0, 4, 0);
        headerGrid.Children.Add(chevron);

        var header = new Border
        {
            Padding = new Thickness(14, 0, 12, 0),
            Background = new SolidColorBrush(Colors.Transparent),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = headerGrid
        };

        var list = new StackPanel
        {
            Margin = new Thickness(28, 2, 12, 8),
            Visibility = Visibility.Collapsed
        };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(list);

        var card = new Border
        {
            Background = Brushes.White,
            BorderBrush = MenuCardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = stack
        };

        header.MouseEnter += (_, _) => UiAnim.BackgroundTo(header, Color.FromRgb(0xED, 0xF1, 0xF6));
        header.MouseLeave += (_, _) => UiAnim.BackgroundTo(header, Colors.Transparent);

        var ui = new ContentRowUi
            { Project = project, Card = card, List = list, Chevron = chevron, State = state };
        _contentRows[project.Id] = ui;

        header.MouseLeftButtonUp += (_, _) => _ = ToggleContentVersionsAsync(project, ui);
        header.ToolTip = $"点一下展开版本列表（{ContentTypes.ProviderName(project.Provider)}）";

        return card;
    }

    /// <summary>图标：先放字母块占位，Modrinth 的图下到后再替换成真图（webp 解不了就保持占位）。</summary>
    private FrameworkElement BuildContentIcon(ContentProject project, int size)
    {
        var letter = (project.Title.Length > 0 ? project.Title[0] : '?').ToString().ToUpperInvariant();
        var block = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = LetterBrush(project.Title),
            Child = new TextBlock
            {
                Text = letter,
                FontSize = size * 0.48,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        if (project.IconUrl is not { Length: > 0 } url) return block;

        _ = Task.Run(async () =>
        {
            var bytes = await ModrinthService.GetIconBytesAsync(url).ConfigureAwait(false);
            if (bytes is null) return;

            try
            {
                var image = new System.Windows.Media.Imaging.BitmapImage();
                using (var stream = new MemoryStream(bytes))
                {
                    image.BeginInit();
                    image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.EndInit();
                }

                image.Freeze();

                await Dispatcher.InvokeAsync(() =>
                    block.Child = new System.Windows.Controls.Image
                    {
                        Source = image,
                        Stretch = System.Windows.Media.Stretch.UniformToFill
                    });
            }
            catch
            {
                // webp 之类的格式解不了，保留字母块
            }
        });

        return block;
    }

    /// <summary>按标题算一个稳定的底色，让占位图标不至于全是同一个颜色。</summary>
    private static SolidColorBrush LetterBrush(string title)
    {
        var palette = new[]
        {
            Color.FromRgb(0x2F, 0x6F, 0x4F), Color.FromRgb(0x3A, 0x6E, 0xA5),
            Color.FromRgb(0x8A, 0x5A, 0x2B), Color.FromRgb(0x6B, 0x4C, 0x9A),
            Color.FromRgb(0x2E, 0x7D, 0x7D), Color.FromRgb(0xA0, 0x4B, 0x5B)
        };

        var sum = title.Sum(c => c);
        return new SolidColorBrush(palette[Math.Abs(sum) % palette.Length]);
    }

    /// <summary>展开 / 收起某个项目的版本列表，第一次展开时才去拉版本。</summary>
    private async Task ToggleContentVersionsAsync(ContentProject project, ContentRowUi ui)
    {
        var expand = !ui.Expanded;
        ui.Expanded = expand;

        UiAnim.Rotate(ui.Chevron, expand);

        if (expand && !ui.VersionsLoaded)
        {
            ui.List.Children.Add(new TextBlock
            {
                Text = "正在获取版本…",
                FontSize = 12,
                Foreground = MenuHintFg,
                Margin = new Thickness(8, 6, 0, 8)
            });

            UiAnim.Expand(ui.List, true);

            try
            {
                var (mc, loader) = ContentFilter();
                var versions = project.Provider == ContentProvider.CurseForge
                    ? await CurseForgeService.GetVersionsAsync(project.Id, mc, loader, _cfg.CfApiKey)
                    : await ModrinthService.GetVersionsAsync(project.Id, mc, loader);

                ui.List.Children.Clear();
                ui.VersionsLoaded = true;

                if (versions.Count == 0)
                {
                    ui.List.Children.Add(new TextBlock
                    {
                        Text = "没有匹配当前过滤条件的版本，把游戏版本或加载器放宽再试试。",
                        FontSize = 12,
                        Foreground = MenuHintFg,
                        Margin = new Thickness(8, 6, 0, 8)
                    });
                    return;
                }

                // 大项目的版本数以千计，只渲染最新的一批
                const int maxRows = 30;
                var shown = versions.Take(maxRows).ToList();

                for (var i = 0; i < shown.Count; i++)
                {
                    var row = BuildContentVersionRow(project, shown[i], ui.State);
                    ui.List.Children.Add(row);
                    UiAnim.FadeIn(row, Math.Min(i, 10) * 20, 6);
                }

                if (versions.Count > shown.Count)
                    ui.List.Children.Add(new TextBlock
                    {
                        Text = $"只列出最新的 {shown.Count} 个版本（共 {versions.Count} 个），把上面的游戏版本选具体一点能少很多。",
                        FontSize = 12,
                        Foreground = MenuHintFg,
                        Margin = new Thickness(8, 2, 0, 6)
                    });
            }
            catch (Exception ex)
            {
                LogBus.Error($"获取 {project.Title} 的版本失败：" + ex.Message);
                ui.List.Children.Clear();
                ui.List.Children.Add(new TextBlock
                {
                    Text = "获取版本失败：" + ex.Message,
                    FontSize = 12,
                    Foreground = MenuHintFg,
                    Margin = new Thickness(8, 6, 0, 8)
                });
            }

            return;
        }

        UiAnim.Expand(ui.List, expand);
    }

    /// <summary>版本行：版本名 + 支持的游戏版本 + 加载器 + 时间 + 大小，点击即下载。</summary>
    private Border BuildContentVersionRow(ContentProject project, ContentVersion version,
        TextBlock state)
    {
        var name = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(version.Name) ? version.VersionNumber : version.Name,
            FontSize = 13,
            Foreground = MenuTitleFg,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var loaders = version.Loaders.Length > 0
            ? string.Join("/", version.Loaders.Select(Upper))
            : "通用";
        var games = version.GameVersions.Length > 0
            ? string.Join(", ", version.GameVersions.Take(3))
            : "—";

        var meta = new TextBlock
        {
            Text = $"{loaders} · {games} · {FormatDate(version.Published)} · {FormatSize(version.Files.Sum(f => f.Size))}",
            FontSize = 12,
            Foreground = MenuHintFg,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(name);
        Grid.SetColumn(meta, 1);
        meta.Margin = new Thickness(12, 0, 0, 0);
        grid.Children.Add(meta);

        var row = new Border
        {
            Height = 42,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 0, 12, 0),
            Margin = new Thickness(0, 0, 0, 4),
            Background = new SolidColorBrush(Colors.Transparent),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = grid
        };

        row.MouseEnter += (_, _) => UiAnim.BackgroundTo(row, Color.FromRgb(0xED, 0xF1, 0xF6));
        row.MouseLeave += (_, _) => UiAnim.BackgroundTo(row, Colors.Transparent);
        row.MouseLeftButtonUp += (_, _) => _ = InstallContentAsync(project, version, row, meta, state);
        row.ToolTip = "点一下下载这个版本";

        return row;
    }

    /// <summary>把一个文件下载到当前整合包对应的目录；已存在则提示。</summary>
    private async Task InstallContentAsync(ContentProject project, ContentVersion version,
        Border row, TextBlock meta, TextBlock state)
    {
        if (_contentInstalling) return;

        var dir = ContentTypes.TargetDir(_cfg, _currentPack?.Id ?? "", _contentKind);
        var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault();
        if (file is null) return;

        var dest = Path.Combine(dir, file.FileName);
        if (File.Exists(dest))
        {
            SetStatus($"{file.FileName} 已经在 {dir} 里了");
            MarkInstalled(state, version);
            return;
        }

        _contentInstalling = true;
        var original = meta.Text;
        meta.Text = "下载中…";
        state.Text = "下载中…";
        row.Cursor = System.Windows.Input.Cursors.Wait;
        SetStatus($"正在下载 {file.FileName}…");

        try
        {
            var path = project.Provider == ContentProvider.CurseForge
                ? await CurseForgeService.DownloadAsync(version, dir)
                : await ModrinthService.DownloadAsync(version, dir);

            // meta 在下载开始时被临时改成「下载中…」，装完必须换回原来的版本信息，
            // 否则行右侧会一直卡在「下载中…」看着像没下完。
            meta.Text = original;
            MarkInstalled(state, version);
            UiAnim.ForegroundTo(meta, Color.FromRgb(0x1F, 0x5C, 0x3F));
            SetStatus($"已安装到 {path}");
            LogBus.Info($"已下载 {ContentTypes.DisplayName(_contentKind)}：{path}");
        }
        catch (Exception ex)
        {
            LogBus.Error($"下载 {file.FileName} 失败：" + ex.Message);
            meta.Text = original;
            state.Text = "选择版本";
            SetStatus($"下载失败：{ex.Message}");
            MessageBox.Show(this, $"下载失败：{ex.Message}", "Override Mirage",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            row.Cursor = System.Windows.Input.Cursors.Hand;
            _contentInstalling = false;
        }
    }

    /// <summary>装完之后把行右侧的状态换成已安装的版本号。</summary>
    private static void MarkInstalled(TextBlock state, ContentVersion version)
    {
        state.Text = $"已安装 {version.VersionNumber}";
        UiAnim.ForegroundTo(state, Color.FromRgb(0x1F, 0x5C, 0x3F));
    }

    private bool _contentInstalling;

    private static string FormatCount(int count) =>
        count >= 1_000_000 ? $"{count / 1_000_000.0:0.0}M" :
        count >= 1_000 ? $"{count / 1_000.0:0.0}k" : count.ToString();

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):0.0} MB" :
        bytes > 0 ? $"{bytes / 1024.0:0.0} KB" : "—";



    /// <summary>下载界面整窗覆盖：每次都从分类视图开始，并确保清单已加载。</summary>
    private void OpenDownloadOverlay()
    {
        PickerOverlay.Visibility = Visibility.Collapsed;
        DownloadInstaller.Visibility = Visibility.Collapsed;
        DownloadContent.Visibility = Visibility.Collapsed;
        DownloadCategories.Visibility = Visibility.Visible;
        SrcMinecraft.IsChecked = true;
        DownloadOverlay.Visibility = Visibility.Visible;

        UiAnim.OverlayIn(DownloadOverlay);
        UiAnim.SlideInX(DownloadCategories, 22, 320, 60);

        _ = LoadDownloadCatalogAsync(false);
    }

    private void BtnCloseDownload_Click(object sender, RoutedEventArgs e) => CloseDownloadOverlay();

    private void CloseDownloadOverlay()
    {
        UiAnim.OverlayOut(DownloadOverlay, RestoreNavSelection);
    }

    /// <summary>关掉覆盖层后把导航高亮还给当前页面，否则「下载」会一直亮着。</summary>
    private void RestoreNavSelection()
    {
        var nav = _currentPage switch
        {
            1 => NavSettings,
            _ => NavLaunch
        };

        nav.IsChecked = true;
    }

    // ---------------- 下载分类（带边框的菜单卡片） ----------------

    private sealed record CategoryDef(string Key, string Title, string Hint);

    private sealed class SectionUi
    {
        public Border Card = null!;
        public Border Header = null!;
        public Border Separator = null!;
        public TextBlock Chevron = null!;
        public TextBlock Title = null!;
        public StackPanel List = null!;
        public int RowCount;
    }

    private static readonly CategoryDef[] CategoryDefs =
    {
        new("latest", "最新版", "最新正式版与快照"),
        new("release", "正式版", "全部稳定版本"),
        new("snapshot", "预览版", "每周快照"),
        new("legacy", "远古版", "Alpha / Beta 时期"),
        new("april", "愚人节版", "历年愚人节整活版本")
    };

    private readonly Dictionary<string, SectionUi> _sections = new();

    private static readonly Brush MenuHoverBg = new SolidColorBrush(Color.FromRgb(0xED, 0xF1, 0xF6));
    private static readonly Brush MenuOpenBg = new SolidColorBrush(Color.FromRgb(0xF3, 0xF9, 0xF5));
    private static readonly Brush MenuTitleFg = new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x21));
    private static readonly Brush MenuOpenFg = new SolidColorBrush(Color.FromRgb(0x1F, 0x5C, 0x3F));
    private static readonly Brush MenuHintFg = new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0xA0));
    private static readonly Brush MenuCardBorder = new SolidColorBrush(Color.FromRgb(0xE4, 0xE8, 0xEE));
    private static readonly Brush MenuCardBorderOpen = new SolidColorBrush(Color.FromRgb(0xB8, 0xD2, 0xC2));

    /// <summary>每个分类一张带边框的卡片：标题行 + 缩进的下级版本列表。</summary>
    private void BuildCategorySections()
    {
        if (_sections.Count > 0) return;

        foreach (var def in CategoryDefs)
        {
            var ui = new SectionUi();

            ui.Chevron = new TextBlock
            {
                Text = "▸",
                FontSize = 13,
                Width = 18,
                Foreground = MenuHintFg,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5)
            };

            ui.Title = new TextBlock
            {
                Text = def.Title,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = MenuTitleFg,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };

            var hint = new TextBlock
            {
                Text = def.Hint,
                FontSize = 12,
                Foreground = MenuHintFg,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.Children.Add(ui.Chevron);
            Grid.SetColumn(ui.Title, 1);
            headerGrid.Children.Add(ui.Title);
            Grid.SetColumn(hint, 2);
            headerGrid.Children.Add(hint);

            ui.Header = new Border
            {
                Height = 58,
                CornerRadius = new CornerRadius(10, 10, 0, 0),
                Padding = new Thickness(16, 0, 16, 0),
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = headerGrid
            };
            ui.Header.MouseEnter += (_, _) =>
            {
                if (ui.List.Visibility != Visibility.Visible)
                    UiAnim.BackgroundTo(ui.Header, Color.FromRgb(0xED, 0xF1, 0xF6));
            };
            ui.Header.MouseLeave += (_, _) =>
            {
                if (ui.List.Visibility != Visibility.Visible)
                    UiAnim.BackgroundTo(ui.Header, Colors.Transparent);
            };
            ui.Header.MouseLeftButtonUp += (_, _) => ToggleSection(def.Key);

            ui.Separator = new Border
            {
                Height = 1,
                Margin = new Thickness(16, 0, 16, 0),
                Background = new SolidColorBrush(Color.FromRgb(0xED, 0xF1, 0xF6)),
                Visibility = Visibility.Collapsed
            };

            ui.List = new StackPanel
            {
                Margin = new Thickness(28, 6, 10, 10),
                Visibility = Visibility.Collapsed
            };

            var stack = new StackPanel();
            stack.Children.Add(ui.Header);
            stack.Children.Add(ui.Separator);
            stack.Children.Add(ui.List);

            ui.Card = new Border
            {
                Background = Brushes.White,
                BorderBrush = MenuCardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 0, 0, 10),
                Child = stack
            };

            DownloadSections.Children.Add(ui.Card);
            _sections[def.Key] = ui;
        }
    }

    private void ToggleSection(string key, bool animate = true)
    {
        if (!_sections.TryGetValue(key, out var ui)) return;

        var expand = ui.List.Visibility != Visibility.Visible;
        if (expand) EnsureSectionRows(key, ui);

        ui.Separator.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        ui.Card.BorderBrush = expand ? MenuCardBorderOpen : MenuCardBorder;

        if (animate)
        {
            // 展开：高度从 0 撑开，箭头转 90°，行逐条淡入
            UiAnim.Expand(ui.List, expand);
            UiAnim.Rotate(ui.Chevron, expand);
            if (expand) StaggerRows(ui.List);

            UiAnim.BackgroundTo(ui.Header, expand
                ? Color.FromRgb(0xF3, 0xF9, 0xF5)
                : Colors.Transparent);
            UiAnim.ForegroundTo(ui.Title, expand
                ? Color.FromRgb(0x1F, 0x5C, 0x3F)
                : Color.FromRgb(0x1B, 0x1D, 0x21));
            return;
        }

        ui.List.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        ui.Header.Background = expand ? new SolidColorBrush(Color.FromRgb(0xF3, 0xF9, 0xF5)) : Brushes.Transparent;
        ui.Title.Foreground = expand
            ? new SolidColorBrush(Color.FromRgb(0x1F, 0x5C, 0x3F))
            : new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x21));
    }

    /// <summary>让一个列表里的行错峰淡入，避免整块内容凭空出现。</summary>
    private static void StaggerRows(Panel list, int stepMs = 18, int maxDelay = 240)
    {
        var delay = 0;
        foreach (var child in list.Children)
        {
            if (child is not FrameworkElement element) continue;
            UiAnim.FadeIn(element, Math.Min(delay, maxDelay), 8);
            delay += stepMs;
        }
    }

    private void EnsureSectionRows(string key, SectionUi ui)
    {
        if (ui.RowCount > 0) return;

        var list = VersionsFor(key);
        if (list.Count == 0)
        {
            ui.List.Children.Add(new TextBlock
            {
                Text = "这个分类暂时没有可用版本。",
                Margin = new Thickness(10, 6, 0, 10),
                FontSize = 12,
                Foreground = MenuHintFg
            });
            ui.RowCount = 1;
            return;
        }

        foreach (var version in list)
            ui.List.Children.Add(BuildMenuVersionRow(key, version));

        ui.RowCount = list.Count;
    }

    /// <summary>卡片内的版本行：缩进、无独立边框，悬停高亮。</summary>
    private Border BuildMenuVersionRow(string category, VersionSummary version)
    {
        var id = new TextBlock
        {
            Text = version.Id,
            FontSize = 13,
            Foreground = MenuTitleFg,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var date = new TextBlock
        {
            Text = FormatDate(version.ReleaseTime),
            FontSize = 12,
            Foreground = MenuHintFg,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(VersionIcons.Create(version, 30));
        Grid.SetColumn(id, 1);
        id.Margin = new Thickness(12, 0, 8, 0);
        grid.Children.Add(id);
        Grid.SetColumn(date, 2);
        grid.Children.Add(date);

        var row = new Border
        {
            Height = 46,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 0, 14, 0),
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = grid
        };

        row.MouseEnter += (_, _) => UiAnim.BackgroundTo(row, Color.FromRgb(0xED, 0xF1, 0xF6));
        row.MouseLeave += (_, _) => UiAnim.BackgroundTo(row, Colors.Transparent);
        row.MouseLeftButtonUp += (_, _) => OpenInstaller(category, version.Id);

        return row;
    }

    /// <summary>清单加载完（或换源）后重建分类卡片，默认展开「最新版」。</summary>
    private void RefreshCategorySections()
    {
        BuildCategorySections();

        foreach (var (key, ui) in _sections)
        {
            ui.List.Children.Clear();
            ui.RowCount = 0;
            ui.List.Visibility = Visibility.Collapsed;
            ui.Separator.Visibility = Visibility.Collapsed;
            ui.Chevron.RenderTransform = null;
            ui.Header.Background = Brushes.Transparent;
            ui.Title.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x21));
            ui.Card.BorderBrush = MenuCardBorder;
        }

        // 首屏不做展开动画，直接就位
        if (VersionsFor("latest").Count > 0) ToggleSection("latest", animate: false);
    }

    private int SectionRowCount(string key) =>
        _sections.TryGetValue(key, out var ui) ? ui.RowCount : 0;

    private static string FormatDate(DateTimeOffset time) =>
        time.ToLocalTime().ToString("yyyy/M/d HH:mm");

    /// <summary>
    /// 进入安装视图：版本已在卡片列表里选好，这里一次配齐「本体 + 加载器」。
    /// 装出来的只有 1 个整合包，原版文件是它的依赖底座，不需要用户单独装。
    /// </summary>
    private void OpenInstaller(string category, string? preferredVersionId = null)
    {
        _currentCategory = category;

        var versions = VersionsFor(category);
        _selectedDlVersion = versions.FirstOrDefault(v => v.Id == preferredVersionId) ?? versions.FirstOrDefault();

        TxtDownloadHint.Text = "";

        ShowDownloadView(DownloadInstaller);

        PrepareLoaderMenus();
        AnimateLoaderMenus();
    }

    private async void BtnDlInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_selectedDlVersion is not { Id: { Length: > 0 } id })
        {
            MessageBox.Show(this, "请先选择一个要下载的版本。", "Override Mirage",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var kind = _dlLoaderKind;
        var loaderVersion = _dlLoaderVersion;

        if (kind is not null && string.IsNullOrWhiteSpace(loaderVersion))
        {
            MessageBox.Show(this, "所选的加载器在当前版本没有可用版本。请换一个加载器，或选「原版」。",
                "Override Mirage", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var packName = TxtDlName.Text.Trim();
        if (packName.Length > 0)
        {
            var error = PackNaming.Validate(packName, _cfg.GameDir);
            if (error is not null)
            {
                // 自动生成的名字撞车（比如同一个组合装第二次）就直接用安装器给的默认 id
                var auto = _dlAutoName is { Length: > 0 } &&
                           string.Equals(packName, _dlAutoName, StringComparison.Ordinal);

                if (!auto)
                {
                    MessageBox.Show(this, error, "Override Mirage",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    TxtDlName.Focus();
                    return;
                }

                packName = "";
            }
        }

        // 一次装完：原版 + 加载器合成一个整合包
        var finalId = await InstallCombinedAsync(id, kind, loaderVersion, packName);
        if (finalId is null) return;

        LoadLocalVersions();
        if (_modpacks.Any(p => p.Id == finalId)) SelectPack(finalId);

        TxtDlName.Clear();
        _dlAutoName = null;
        UpdateDlSummary();
    }

    /// <summary>
    /// 一体化安装：Minecraft 本体（+ 可选加载器）一次装完，返回最终整合包 id，失败返回 null。
    /// 选了加载器时原版由 LoaderService 内部先补齐，全程只有一条进度、一次结果。
    /// </summary>
    private async Task<string?> InstallCombinedAsync(string mcVersion, LoaderKind? kind, string? loaderVersion,
        string packName)
    {
        if (_busy) return null;

        SaveConfigFromUi();

        var label = CombinedInstaller.Describe(mcVersion, kind, loaderVersion);

        _busy = true;
        SetBusy(true);
        Prg.Value = 0;

        var progress = new Progress<InstallProgress>(p =>
        {
            Prg.Value = p.Percent;
            SetStatus($"[{label}] {p.Stage}");
        });

        try
        {
            SetStatus($"正在安装 {label}…");

            var installedId = await CombinedInstaller.InstallAsync(_cfg, mcVersion, kind, loaderVersion,
                packName, progress, LogBus.Info, CancellationToken.None);

            Prg.Value = 100;
            SetStatus($"{label} 安装完成");
            LogBus.Info($"{label} 安装完成：{installedId}");
            return installedId;
        }
        catch (Exception ex)
        {
            LogBus.Error($"安装 {label} 失败：" + ex);
            SetStatus($"安装 {label} 失败：" + ex.Message);
            MessageBox.Show(this, $"安装失败：\n\n{ex.Message}", "Override Mirage",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    // ---------------- 安装视图的加载器菜单 ----------------

    private sealed class LoaderMenuUi
    {
        public LoaderKind Kind;
        public Border Card = null!;
        public Border Header = null!;
        public Border Separator = null!;
        public TextBlock Chevron = null!;
        public TextBlock Hint = null!;
        public StackPanel List = null!;
        public bool Loading;
        public bool Loaded;
        public List<string> Versions = new();
    }

    private static readonly LoaderKind[] LoaderOrder =
    {
        LoaderKind.NeoForge, LoaderKind.Forge, LoaderKind.Fabric, LoaderKind.Quilt
    };

    private readonly Dictionary<LoaderKind, LoaderMenuUi> _loaderMenus = new();
    private string? _dlLoaderVersion;
    private string? _dlAutoName;

    /// <summary>进入安装视图：清空选择，四行加载器同时开始拉版本（镜像优先）。</summary>
    private void PrepareLoaderMenus()
    {
        BuildLoaderMenus();

        _dlLoaderKind = null;
        _dlLoaderVersion = null;
        _dlAutoName = null;
        TxtDlName.Clear();
        DlNameIcon.Child = ModpackIcons.Create(new ModpackEntry("", null, null), 34);
        TxtDownloadHint.Text = "";

        var mc = _selectedDlVersion?.Id;
        foreach (var (_, ui) in _loaderMenus)
        {
            ui.Versions.Clear();
            ui.Loaded = false;
            ui.Loading = false;
            ui.List.Children.Clear();
            ui.List.Visibility = Visibility.Collapsed;
            ui.Separator.Visibility = Visibility.Collapsed;
            ui.Chevron.Text = "\u25b8";
            ui.Header.Background = Brushes.Transparent;
            ui.Card.BorderBrush = MenuCardBorder;
            ui.Hint.Text = mc is null ? "无版本" : "查询中\u2026";
        }

        UpdateDlSummary();

        if (string.IsNullOrWhiteSpace(mc)) return;

        foreach (var kind in LoaderOrder)
            _ = LoadLoaderVersionsAsync(kind, mc);
    }

    /// <summary>安装视图刚打开时，四行加载器菜单依次淡入。</summary>
    private void AnimateLoaderMenus()
    {
        var delay = 0;
        foreach (var kind in LoaderOrder)
        {
            if (_loaderMenus.TryGetValue(kind, out var ui))
            {
                UiAnim.FadeIn(ui.Card, delay, 10);
                delay += 40;
            }
        }
    }

    private void BuildLoaderMenus()
    {
        if (_loaderMenus.Count > 0) return;

        foreach (var kind in LoaderOrder)
        {
            var ui = new LoaderMenuUi { Kind = kind };

            ui.Chevron = new TextBlock
            {
                Text = "\u25b8",
                FontSize = 12,
                Width = 16,
                Foreground = MenuHintFg,
                VerticalAlignment = VerticalAlignment.Center
            };

            var title = new TextBlock
            {
                Text = LoaderService.DisplayName(kind),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = MenuTitleFg,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };

            ui.Hint = new TextBlock
            {
                Text = "查询中\u2026",
                FontSize = 11,
                Foreground = MenuHintFg,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.Children.Add(LoaderIcons.Create(kind, 26));
            Grid.SetColumn(title, 1);
            headerGrid.Children.Add(title);
            Grid.SetColumn(ui.Hint, 2);
            headerGrid.Children.Add(ui.Hint);
            ui.Chevron.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(ui.Chevron, 3);
            headerGrid.Children.Add(ui.Chevron);

            ui.Header = new Border
            {
                Height = 50,
                CornerRadius = new CornerRadius(10, 10, 0, 0),
                Padding = new Thickness(14, 0, 14, 0),
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = headerGrid
            };
            var uiRef = ui;
            ui.Header.MouseEnter += (_, _) =>
            {
                if (uiRef.List.Visibility != Visibility.Visible) uiRef.Header.Background = MenuHoverBg;
            };
            ui.Header.MouseLeave += (_, _) =>
            {
                if (uiRef.List.Visibility != Visibility.Visible) uiRef.Header.Background = Brushes.Transparent;
            };
            ui.Header.MouseLeftButtonUp += (_, _) => ToggleLoaderMenu(uiRef);

            ui.Separator = new Border
            {
                Height = 1,
                Margin = new Thickness(14, 0, 14, 0),
                Background = new SolidColorBrush(Color.FromRgb(0xED, 0xF1, 0xF6)),
                Visibility = Visibility.Collapsed
            };

            ui.List = new StackPanel
            {
                Margin = new Thickness(26, 4, 8, 8),
                Visibility = Visibility.Collapsed
            };

            var stack = new StackPanel();
            stack.Children.Add(ui.Header);
            stack.Children.Add(ui.Separator);
            stack.Children.Add(ui.List);

            ui.Card = new Border
            {
                Background = Brushes.White,
                BorderBrush = MenuCardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = stack
            };

            LoaderSections.Children.Add(ui.Card);
            _loaderMenus[kind] = ui;
        }
    }

    private async Task LoadLoaderVersionsAsync(LoaderKind kind, string mcVersion)
    {
        var ui = _loaderMenus[kind];
        ui.Loading = true;
        ui.Hint.Text = "查询中\u2026";

        try
        {
            // 安装界面的加载器版本优先走国内镜像，快且不受官方源波动影响
            var versions = await LoaderService.GetLoaderVersionsAsync(kind, mcVersion, preferMirror: true);
            ui.Versions = versions.ToList();
            ui.Loaded = true;
            ui.Loading = false;

            UpdateLoaderMenuHints();
            if (ui.List.Visibility == Visibility.Visible) FillLoaderList(ui);

            LogBus.Info($"{LoaderService.DisplayName(kind)} 在 {mcVersion} 的版本数：{ui.Versions.Count}");
        }
        catch (Exception ex)
        {
            ui.Loaded = true;
            ui.Loading = false;
            LogBus.Warn($"查询 {LoaderService.DisplayName(kind)} 版本失败：" + ex.Message);
            UpdateLoaderMenuHints();
            if (ui.List.Visibility == Visibility.Visible) FillLoaderList(ui);
        }
    }

    private void ToggleLoaderMenu(LoaderMenuUi ui)
    {
        var expand = ui.List.Visibility != Visibility.Visible;

        if (expand)
        {
            FillLoaderList(ui);
            ui.Separator.Visibility = Visibility.Visible;
        }
        else
        {
            ui.Separator.Visibility = Visibility.Collapsed;
        }

        ui.List.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        ui.Chevron.Text = expand ? "\u25be" : "\u25b8";
        ui.Header.Background = expand ? MenuOpenBg : Brushes.Transparent;
        ui.Card.BorderBrush = expand ? MenuCardBorderOpen : MenuCardBorder;
    }

    private void FillLoaderList(LoaderMenuUi ui)
    {
        ui.List.Children.Clear();

        if (!ui.Loaded)
        {
            ui.List.Children.Add(new TextBlock
            {
                Text = "正在获取版本列表\u2026",
                FontSize = 12,
                Foreground = MenuHintFg,
                Margin = new Thickness(10, 6, 0, 8)
            });
            return;
        }

        if (ui.Versions.Count == 0)
        {
            ui.List.Children.Add(new TextBlock
            {
                Text = "这个游戏版本没有可用的加载器版本。",
                FontSize = 12,
                Foreground = MenuHintFg,
                Margin = new Thickness(10, 6, 0, 8)
            });
            return;
        }

        foreach (var version in ui.Versions)
        {
            var selected = _dlLoaderKind == ui.Kind && _dlLoaderVersion == version;

            var text = new TextBlock
            {
                Text = version,
                FontSize = 13,
                Foreground = selected ? MenuOpenFg : MenuTitleFg,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };

            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.Children.Add(LoaderIcons.Create(ui.Kind, 18));
            Grid.SetColumn(text, 1);
            rowGrid.Children.Add(text);

            var row = new Border
            {
                Height = 36,
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 0, 12, 0),
                Margin = new Thickness(0, 0, 0, 2),
                Background = selected
                    ? new SolidColorBrush(Color.FromRgb(0xF1, 0xF8, 0xF4))
                    : Brushes.Transparent,
                BorderBrush = selected ? MenuCardBorderOpen : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = rowGrid
            };

            var uiRef = ui;
            var ver = version;
            row.MouseEnter += (_, _) =>
            {
                if (!(_dlLoaderKind == uiRef.Kind && _dlLoaderVersion == ver))
                    row.Background = MenuHoverBg;
            };
            row.MouseLeave += (_, _) =>
            {
                if (!(_dlLoaderKind == uiRef.Kind && _dlLoaderVersion == ver))
                    row.Background = Brushes.Transparent;
            };
            row.MouseLeftButtonUp += (_, _) => SelectLoaderVersion(uiRef, ver);

            ui.List.Children.Add(row);
        }
    }

    private void SelectLoaderVersion(LoaderMenuUi ui, string version)
    {
        _dlLoaderKind = ui.Kind;
        _dlLoaderVersion = version;

        // 选完收起菜单，头部显示已选版本
        ui.List.Visibility = Visibility.Collapsed;
        ui.Separator.Visibility = Visibility.Collapsed;
        ui.Chevron.Text = "\u25b8";
        ui.Header.Background = Brushes.Transparent;
        ui.Card.BorderBrush = MenuCardBorder;

        DlNameIcon.Child = LoaderIcons.Create(ui.Kind, 34);
        UpdateLoaderMenuHints();
        UpdateDlNameSuggestion();
        UpdateDlSummary();
        SetStatus($"已选择 {LoaderService.DisplayName(ui.Kind)} {version}");
    }

    private void UpdateLoaderMenuHints()
    {
        foreach (var (kind, ui) in _loaderMenus)
        {
            if (_dlLoaderKind == kind && _dlLoaderVersion is { } selected)
            {
                ui.Hint.Text = $"已选 {selected}";
                ui.Hint.Foreground = MenuOpenFg;
            }
            else
            {
                ui.Hint.Foreground = MenuHintFg;
                if (!ui.Loaded) ui.Hint.Text = ui.Loading ? "查询中\u2026" : "待查询";
                else ui.Hint.Text = ui.Versions.Count > 0 ? $"共 {ui.Versions.Count} 个版本" : "无可用版本";
            }
        }
    }

    /// <summary>
    /// 选了加载器就自动给出一个「版本-加载器-加载器版本」的名字；
    /// 只在名称框为空或仍是上次自动生成的值时覆盖，用户自己写的名字不动。
    /// </summary>
    private void UpdateDlNameSuggestion()
    {
        if (_dlLoaderKind is not { } kind || string.IsNullOrWhiteSpace(_dlLoaderVersion))
        {
            if (_dlAutoName is { Length: > 0 } && string.Equals(TxtDlName.Text, _dlAutoName, StringComparison.Ordinal))
                TxtDlName.Clear();
            _dlAutoName = null;
            return;
        }

        var mc = _selectedDlVersion?.Id ?? "";
        var suggest = $"{mc}-{LoaderService.DisplayName(kind)}-{_dlLoaderVersion}";

        if (TxtDlName.Text.Length == 0 ||
            (_dlAutoName is { Length: > 0 } && string.Equals(TxtDlName.Text, _dlAutoName, StringComparison.Ordinal)))
        {
            TxtDlName.Text = suggest;
            _dlAutoName = suggest;
        }
    }

    /// <summary>把「这次到底装什么」写成一句话：本体 + 加载器 = 一个整合包。</summary>
    private void UpdateDlSummary()
    {
        var mc = _selectedDlVersion?.Id ?? "未选择";

        TxtDlSummary.Text = _dlLoaderKind switch
        {
            { } kind when !string.IsNullOrWhiteSpace(_dlLoaderVersion) =>
                $"将安装 1 个整合包：Minecraft {mc} + {LoaderService.DisplayName(kind)} {_dlLoaderVersion}。" +
                "原版文件会作为依赖一起装好，不需要再单独装一次本体。",
            _ =>
                $"将安装 Minecraft {mc} 原版。要玩模组就展开下面任意一个加载器选一个版本，它会和本体一起装成同一个整合包。"
        };
    }

    private void BtnDlBack_Click(object sender, RoutedEventArgs e)
    {
        ShowDownloadView(DownloadCategories);
    }

    private void CmbSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSource) return;

        var mirror = CmbSource.SelectedIndex == 1;
        if (_cfg.UseMirror == mirror) return;

        _cfg.UseMirror = mirror;
        _cfg.Save();

        DownloadSource.Configure(mirror);
        VersionService.ResetCache();
        _downloadLoaded = false;

        SetStatus(mirror ? "已切换到 BMCLAPI 镜像，重新查询即可生效" : "已切换回官方源");

        if (DownloadOverlay.Visibility == Visibility.Visible) _ = LoadDownloadCatalogAsync(true);
    }

    private List<VersionSummary> VersionsFor(string category) => category switch
    {
        "latest" => _allVersions
            .Where(v => v.Id == _latestReleaseId || v.Id == _latestSnapshotId)
            .ToList(),

        "release" => _allVersions.Where(v => v.Type == "release").ToList(),

        // 预览版排除愚人节整活
        "snapshot" => _allVersions
            .Where(v => v.Type == "snapshot" && !AprilFoolsIds.Contains(v.Id))
            .ToList(),

        "legacy" => _allVersions
            .Where(v => v.Type is "old_beta" or "old_alpha")
            .ToList(),

        "april" => _allVersions.Where(v => AprilFoolsIds.Contains(v.Id)).ToList(),

        _ => new List<VersionSummary>()
    };

    private void BtnBrowseJava_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 javaw.exe",
            Filter = "Java 可执行文件|javaw.exe;java.exe|可执行文件|*.exe"
        };

        if (dialog.ShowDialog(this) != true) return;

        var major = JavaFinder.GetMajorVersion(dialog.FileName);
        if (major <= 0)
        {
            MessageBox.Show(this, "无法识别该文件的 Java 版本。", "Override Mirage",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _javas.Insert(0, new JavaInfo(dialog.FileName, major));
        CmbJava.ItemsSource = null;
        CmbJava.ItemsSource = _javas;
        CmbJava.DisplayMemberPath = nameof(JavaInfo.Display);
        CmbJava.SelectedIndex = 0;
        LogBus.Info($"手动指定 Java {major}：{dialog.FileName}");
    }

    private void BtnBrowseGameDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择游戏目录" };
        if (dialog.ShowDialog(this) != true) return;

        TxtGameDir.Text = dialog.FolderName;
        _cfg.GameDir = dialog.FolderName;
        _downloadLoaded = false;
        LoadLocalVersions();
        LogBus.Info($"游戏目录切换为 {dialog.FolderName}");
    }

    private void BtnOpenGameDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = TxtGameDir.Text.Trim();
        if (!Directory.Exists(dir))
        {
            MessageBox.Show(this, "该目录不存在。", "Override Mirage",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogBus.Error("打开目录失败：" + ex.Message);
        }
    }

    // ---------------- 登录（正版 / 离线） ----------------

    /// <summary>把配置里的登录方式同步到分段控件与两个面板。</summary>
    private void ApplyAuthMode()
    {
        _syncingAuth = true;
        var microsoft = _cfg.AuthMode == "microsoft";
        SegMicrosoft.IsChecked = microsoft;
        SegOffline.IsChecked = !microsoft;
        _syncingAuth = false;

        PaneMicrosoft.Visibility = microsoft ? Visibility.Visible : Visibility.Collapsed;
        PaneOffline.Visibility = microsoft ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AuthMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingAuth || sender is not RadioButton { Tag: string mode } || _cfg.AuthMode == mode) return;

        _cfg.AuthMode = mode;
        _cfg.Save();

        var microsoft = mode == "microsoft";
        PaneMicrosoft.Visibility = microsoft ? Visibility.Visible : Visibility.Collapsed;
        PaneOffline.Visibility = microsoft ? Visibility.Collapsed : Visibility.Visible;
        UpdateMicrosoftUi();

        SetStatus(microsoft ? "已切到正版登录" : "已切到离线登录");
    }

    /// <summary>按登录状态切换正版面板的三个子视图。</summary>
    private void UpdateMicrosoftUi()
    {
        var signedIn = _cfg.HasMicrosoftAccount;
        var loggingIn = _msLoginCts is not null;

        MsSignedOut.Visibility = !signedIn && !loggingIn ? Visibility.Visible : Visibility.Collapsed;
        MsSigningIn.Visibility = loggingIn ? Visibility.Visible : Visibility.Collapsed;
        MsSignedIn.Visibility = signedIn && !loggingIn ? Visibility.Visible : Visibility.Collapsed;

        if (signedIn)
        {
            TxtMsName.Text = _cfg.MsName;
            var state = _cfg.MsExpiresAt > DateTimeOffset.UtcNow ? "凭证有效" : "凭证待续期";
            TxtMsUuid.Text = $"UUID {_cfg.MsUuid} · {state}";
        }

        // 应用 ID 有内置的兜底，这里只提示登录本身可能遇到的问题
        TxtMsHint.Text = "";
    }

    private async void BtnMsLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_msLoginCts is not null) return;

        // 不内置任何应用 ID：没填就提示用户去 Azure 注册一个自己的
        var clientId = _cfg.EffectiveMsClientId;
        if (!MicrosoftAuth.HasClientId(clientId))
        {
            SetStatus("正版登录需要先配置 Azure 应用 ID");
            MessageBox.Show(
                "正版登录需要你自己的 Azure 应用 ID（免费，两分钟）：\n\n" +
                "1. 打开 https://portal.azure.com → 应用注册 → 新注册\n" +
                "2. 账户类型选「任何组织目录中的账户和个人 Microsoft 账户」\n" +
                "3. 注册完进入「身份验证」→ 打开「允许公共客户端流」并保存\n" +
                "4. 复制概览页的「应用程序(客户端) ID」，粘贴到本启动器的「设置 → 应用 ID」并保存\n\n" +
                "令牌只保存在你自己电脑的 config.json 里，本启动器不内置也不上传任何账号信息。",
                "还没有配置 Azure 应用 ID", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _msLoginCts = new CancellationTokenSource();
        TxtMsCode.Text = "--------";
        TxtMsUrl.Text = "";
        TxtMsWait.Text = "正在申请设备码…";
        UpdateMicrosoftUi();
        SetStatus("正在申请 Microsoft 设备码…");

        try
        {
            var device = await MicrosoftAuth.StartDeviceCodeAsync(clientId, _msLoginCts.Token);

            TxtMsCode.Text = device.UserCode;
            TxtMsUrl.Text = device.VerificationUri;
            TxtMsWait.Text = $"等待你在浏览器里完成授权（{Math.Max(1, device.ExpiresInSeconds / 60)} 分钟内有效）…";

            OpenUrl(device.VerificationUri);

            var progress = new Progress<string>(text =>
            {
                if (_msLoginCts is { IsCancellationRequested: false }) TxtMsWait.Text = text;
            });

            // 每一步的结果都进日志：界面上只看到一句话，排查要看 logs/launcher-*.log
            var account = await MicrosoftAuth.WaitForLoginAsync(
                clientId, device, progress, _msLoginCts.Token, LogBus.Info);
            ApplyMicrosoftAccount(account);
            SetStatus($"已登录正版账号：{account.Name}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消正版登录");
        }
        catch (Exception ex)
        {
            LogBus.Error("正版登录失败：" + ex.Message);
            SetStatus("正版登录失败：" + ex.Message);
            MessageBox.Show(this,
                "正版登录失败：\n\n" + ex.Message + "\n\n" +
                "每一步的结果都写在 logs/launcher-*.log，也可以执行 --mslogin 逐步诊断。",
                "Override Mirage", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _msLoginCts?.Dispose();
            _msLoginCts = null;
            UpdateMicrosoftUi();
        }
    }

    private void BtnMsCopyCode_Click(object sender, RoutedEventArgs e)
    {
        var code = TxtMsCode.Text;
        if (string.IsNullOrWhiteSpace(code) || code == "--------") return;

        try
        {
            Clipboard.SetText(code);
            SetStatus("授权码已复制");
        }
        catch (Exception ex)
        {
            LogBus.Warn("复制授权码失败：" + ex.Message);
        }
    }

    private void BtnMsOpenPage_Click(object sender, RoutedEventArgs e) => OpenUrl(TxtMsUrl.Text);

    private void BtnMsCancel_Click(object sender, RoutedEventArgs e)
    {
        TxtMsWait.Text = "正在取消…";
        _msLoginCts?.Cancel();
    }

    private async void BtnMsRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_cfg.MsRefreshToken))
        {
            MessageBox.Show(this, "没有可用的刷新凭证，请重新登录。", "Override Mirage",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        BtnMsRefresh.IsEnabled = false;
        SetStatus("正在刷新正版凭证…");
        try
        {
            var account = await MicrosoftAuth.RefreshAsync(_cfg.EffectiveMsClientId, _cfg.MsRefreshToken,
                CancellationToken.None);
            ApplyMicrosoftAccount(account);
            SetStatus("正版凭证已刷新");
        }
        catch (Exception ex)
        {
            LogBus.Error("刷新正版凭证失败：" + ex.Message);
            SetStatus("刷新失败：" + ex.Message);
            MessageBox.Show(this, "刷新凭证失败，建议重新登录：\n\n" + ex.Message, "Override Mirage",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            BtnMsRefresh.IsEnabled = true;
        }
    }

    private void BtnMsLogout_Click(object sender, RoutedEventArgs e)
    {
        ClearMicrosoftAccount();
        _cfg.AuthMode = "offline";
        _cfg.Save();

        ApplyAuthMode();
        UpdateMicrosoftUi();
        SetStatus("已退出正版账号，回到离线登录");
    }

    private void BtnSaveClientId_Click(object sender, RoutedEventArgs e)
    {
        _cfg.MsClientId = TxtClientId.Text.Trim();
        _cfg.Save();
        UpdateMicrosoftUi();
        SetStatus(_cfg.MsClientId.Length > 0
            ? "应用 ID 已保存"
            : "应用 ID 已清空，将使用启动器自带的应用 ID");
    }

    /// <summary>启动时静默续期，避免每次都要重新授权。</summary>
    private async Task TrySilentRefreshAsync()
    {
        if (_cfg.AuthMode != "microsoft" || !_cfg.HasMicrosoftAccount) return;
        if (_cfg.MsExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(10)) return;
        if (string.IsNullOrWhiteSpace(_cfg.MsRefreshToken)) return;

        try
        {
            var account = await MicrosoftAuth.RefreshAsync(_cfg.EffectiveMsClientId, _cfg.MsRefreshToken,
                CancellationToken.None);
            ApplyMicrosoftAccount(account);
            LogBus.Info($"正版凭证已自动续期：{account.Name}");
        }
        catch (Exception ex)
        {
            LogBus.Warn("正版凭证自动续期失败：" + ex.Message);
            UpdateMicrosoftUi();
        }
    }

    /// <summary>启动前确定用哪个账号；凭证过期会先续期，失败则中止启动。</summary>
    private async Task<AuthSession?> ResolveSessionAsync()
    {
        if (_cfg.AuthMode != "microsoft")
            return AuthSession.Offline(OfflineName());

        if (!_cfg.HasMicrosoftAccount)
        {
            MessageBox.Show(this, "还没有登录正版账号。请先完成正版登录，或切到离线登录。", "Override Mirage",
                MessageBoxButton.OK, MessageBoxImage.Information);
            NavLaunch.IsChecked = true;
            return null;
        }

        if (_cfg.MsExpiresAt - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(5) &&
            !string.IsNullOrWhiteSpace(_cfg.MsRefreshToken))
        {
            SetStatus("正版凭证即将过期，正在续期…");
            try
            {
                var refreshed = await MicrosoftAuth.RefreshAsync(_cfg.EffectiveMsClientId, _cfg.MsRefreshToken,
                    CancellationToken.None);
                ApplyMicrosoftAccount(refreshed);
            }
            catch (Exception ex)
            {
                LogBus.Error("正版凭证续期失败：" + ex.Message);
                SetStatus("正版凭证续期失败，请重新登录");
                MessageBox.Show(this, "正版凭证已失效，请重新登录：\n\n" + ex.Message, "Override Mirage",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                NavLaunch.IsChecked = true;
                return null;
            }
        }

        return AuthSession.Microsoft(_cfg.MsName, _cfg.MsUuid, _cfg.MsAccessToken, _cfg.MsXuid,
            _cfg.EffectiveMsClientId);
    }

    private void ApplyMicrosoftAccount(MicrosoftAccount account)
    {
        _cfg.MsName = account.Name;
        _cfg.MsUuid = account.Uuid;
        _cfg.MsAccessToken = account.AccessToken;
        _cfg.MsRefreshToken = account.RefreshToken;
        _cfg.MsExpiresAt = account.ExpiresAt;
        _cfg.AuthMode = "microsoft";
        _cfg.Save();

        ApplyAuthMode();
        UpdateMicrosoftUi();
    }

    private void ClearMicrosoftAccount()
    {
        _cfg.MsName = "";
        _cfg.MsUuid = "";
        _cfg.MsAccessToken = "";
        _cfg.MsRefreshToken = "";
        _cfg.MsXuid = "";
        _cfg.MsExpiresAt = default;
    }

    private string OfflineName() => string.IsNullOrWhiteSpace(TxtName.Text) ? "Player" : TxtName.Text.Trim();

    private void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogBus.Error("打开链接失败：" + ex.Message);
        }
    }

    // ---------------- 整合包设置 ----------------

    private void BtnModpackSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;

        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void MenuOpenPackDir_Click(object sender, RoutedEventArgs e) => OpenDirectory(_cfg.GameDir);

    private void MenuOpenModsDir_Click(object sender, RoutedEventArgs e)
    {
        // 开了版本隔离就是当前整合包专属的 mods 目录
        var modsDir = _cfg.ModsDir(_currentPack?.Id ?? "");
        try
        {
            Directory.CreateDirectory(modsDir);
        }
        catch (Exception ex)
        {
            LogBus.Error("创建 mods 目录失败：" + ex.Message);
            return;
        }

        OpenDirectory(modsDir);
    }

    private async void MenuRepairPack_Click(object sender, RoutedEventArgs e) => await InstallAsync();

    /// <summary>装 / 换加载器统一走「下载」页：选版本 → 选加载器 → 安装。</summary>
    private void MenuLoaderSettings_Click(object sender, RoutedEventArgs e) => OpenDownloadOverlay();

    private void OpenDirectory(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            MessageBox.Show(this, "目录不存在：" + dir, "Override Mirage",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogBus.Error("打开目录失败：" + ex.Message);
        }
    }

    private async void BtnLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!TryGetVersion(out var version)) return;

        SaveConfigFromUi();

        if (CmbJava.SelectedItem is not JavaInfo java)
        {
            MessageBox.Show(this, "请先到「设置」里选择一个 Java 运行时。", "Override Mirage",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var session = await ResolveSessionAsync();
        if (session is null) return;

        var jsonPath = Path.Combine(Paths.VersionsDir(_cfg.GameDir), version.Id, version.Id + ".json");

        if (!File.Exists(jsonPath))
        {
            var answer = MessageBox.Show(this,
                $"{version.Id} 尚未安装，是否现在下载安装？",
                "Override Mirage", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes) return;

            await InstallAsync();
            if (!File.Exists(jsonPath)) return;
        }

        try
        {
            SetStatus("正在解析版本…");
            var resolved = await VersionResolver.ResolveAsync(_cfg.GameDir, version.Id);

            // NeoForge 的运行依赖（universal / srg）可能缺失，启动前先补一次；
            // 补过之后版本 JSON 变了，需要重新解析才能带上新的 classpath。
            if (await NeoForgeRuntime.EnsureAsync(_cfg.GameDir, version.Id, resolved.MergedJson, LogBus.Info))
                resolved = await VersionResolver.ResolveAsync(_cfg.GameDir, version.Id);

            var jarPath = Path.Combine(Paths.VersionsDir(_cfg.GameDir), resolved.JarId, resolved.JarId + ".jar");
            if (!File.Exists(jarPath))
            {
                MessageBox.Show(this,
                    $"缺少客户端 jar（{resolved.JarId}.jar），请到「版本与模组」页点「校验 / 修复」补全。",
                    "Override Mirage", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var required = 8;
            try
            {
                using var doc = JsonDocument.Parse(resolved.MergedJson);
                required = VersionJson.GetRequiredJavaMajor(doc.RootElement);
            }
            catch
            {
                // 忽略，按默认值处理
            }

            if (java.MajorVersion < required)
            {
                var answer = MessageBox.Show(this,
                    $"该版本建议使用 Java {required} 或更高，当前选中 Java {java.MajorVersion}，可能无法启动。\n\n仍要继续吗？",
                    "Override Mirage", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (answer != MessageBoxResult.Yes) return;
            }

            SetStatus("正在启动游戏…");
            LogBus.Info($"以 {(_cfg.AuthMode == "microsoft" ? "正版" : "离线")} 身份启动：{session.PlayerName} / {session.Uuid}");
            var process = GameLauncher.Launch(_cfg, resolved, java, LogBus.Info, session);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                LogBus.Info($"游戏进程已退出，退出码 {SafeExitCode(process)}");
                SetStatus("游戏已退出");
            }));

            SetStatus($"游戏已启动（PID {process.Id}）");
        }
        catch (Exception ex)
        {
            LogBus.Error("启动失败：" + ex);
            SetStatus("启动失败：" + ex.Message);
            MessageBox.Show(this, "启动失败：\n\n" + ex.Message, "Override Mirage",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------------- 安装 ----------------

    /// <summary>「校验 / 修复」：对左下角当前选中的整合包走一遍安装流程。</summary>
    private async Task InstallAsync()
    {
        if (_busy) return;
        if (!TryGetVersion(out var version)) return;

        await InstallVersionAsync(version.Id);
    }

    private async Task<bool> InstallVersionAsync(string versionId)
    {
        if (_busy) return false;

        SaveConfigFromUi();

        _busy = true;
        SetBusy(true);
        Prg.Value = 0;

        var progress = new Progress<InstallProgress>(p =>
        {
            Prg.Value = p.Percent;
            SetStatus(p.Stage);
        });

        try
        {
            LogBus.Info($"开始安装 {versionId}");
            await MinecraftInstaller.InstallAsync(_cfg, versionId, progress, CancellationToken.None);
            Prg.Value = 100;
            SetStatus($"{versionId} 安装完成");
            return true;
        }
        catch (Exception ex)
        {
            LogBus.Error("安装失败：" + ex);
            SetStatus("安装失败：" + ex.Message);
            MessageBox.Show(this, "安装失败：\n\n" + ex.Message, "Override Mirage",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    // ---------------- 辅助 ----------------

    private bool TryGetVersion(out VersionSummary version)
    {
        version = _currentPack is null
            ? new VersionSummary()
            : new VersionSummary { Id = _currentPack.Id, Type = "local" };

        if (_currentPack is not null) return true;

        if (_modpacks.Count == 0)
        {
            MessageBox.Show(this, "本地还没有整合包。先去「下载」页装一个版本。", "Override Mirage",
                MessageBoxButton.OK, MessageBoxImage.Information);
            NavDownload.IsChecked = true;
        }
        else
        {
            MessageBox.Show(this, "请先在左下角点「选择整合包」挑一个。", "Override Mirage",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        return false;
    }

    /// <summary>用户名一改就落盘，避免关掉窗口后回到默认值。</summary>
    private void TxtName_TextChanged(object sender, TextChangedEventArgs e)
    {
        _nameSaveTimer.Stop();
        _nameSaveTimer.Start();
    }

    private void SavePlayerName()
    {
        var name = TxtName.Text.Trim();
        _cfg.PlayerName = name.Length == 0 ? "Player" : name;
        _cfg.Save();
    }

    private void SaveConfigFromUi()
    {
        SavePlayerName();
        _cfg.VersionId = _currentPack?.Id ?? _cfg.VersionId;
        _cfg.GameDir = string.IsNullOrWhiteSpace(TxtGameDir.Text)
            ? Path.Combine(Paths.BaseDir, ".minecraft")
            : TxtGameDir.Text.Trim();
        _cfg.JavaPath = (CmbJava.SelectedItem as JavaInfo)?.Path ?? _cfg.JavaPath;
        _cfg.MemoryMb = int.TryParse(TxtMemory.Text, out var memory)
            ? Math.Clamp(memory, 512, 65536)
            : 4096;
        _cfg.MsClientId = TxtClientId.Text.Trim();
        _cfg.Save();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BtnLaunch.IsEnabled = !busy;
        BtnDlInstall.IsEnabled = !busy;

        if (busy)
        {
            StatusStrip.Visibility = Visibility.Visible;
            _statusTimer.Stop();
        }
        else if (StatusStrip.Visibility == Visibility.Visible)
        {
            _statusTimer.Start();
        }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }
}

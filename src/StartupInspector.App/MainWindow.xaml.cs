using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using StartupInspector.Core;

namespace StartupInspector.App;

public partial class MainWindow : Window
{
    /// <summary>左栏读数与筛选用的语义色,和 XAML 里是同一套。</summary>
    private static readonly Brush AmberText = Freeze("#FBBF24");
    private static readonly Brush OkText = Freeze("#7DD3A0");
    private static readonly Brush ErrorText = Freeze("#FB7185");

    private readonly StartupScanner _scanner = new();
    private readonly StartupController _controller = new();
    private readonly ObservableCollection<EntryRow> _rows = new();
    private readonly ICollectionView _view;

    /// <summary>左栏的筛选状态:all / on / off。</summary>
    private string _stateFilter = "all";

    /// <summary>是否按开机耗时从大到小排序。</summary>
    private bool _sortByImpact;

    public MainWindow()
    {
        InitializeComponent();
        
        ApplyStartupAppearance();

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterPredicate;
        Grid.ItemsSource = _view;

        SourceFilter.Items.Add("全部来源");
        foreach (var source in Enum.GetValues<StartupSource>())
            SourceFilter.Items.Add(StartupLabels.SourceText(source));
        SourceFilter.SelectedIndex = 0;

        Loaded += (_, _) => Rescan();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowMaterial.Apply(this);
    }

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    // ---------------- 面板内提示条(替代系统弹窗) ----------------

    private enum NoticeKind { Info, Success, Warning, Error, Confirm }

    private TaskCompletionSource<bool>? _pendingConfirm;
    private DispatcherTimer? _noticeTimer;

    private void NotifyInfo(string message, string? detail = null) => ShowNotice(NoticeKind.Info, message, detail, autoHide: true);
    private void NotifySuccess(string message, string? detail = null) => ShowNotice(NoticeKind.Success, message, detail, autoHide: true);
    private void NotifyError(string message, string? detail = null) => ShowNotice(NoticeKind.Error, message, detail, autoHide: false);

    /// <summary>弹一条确认并等用户点"是/否";被新的提示顶掉时按"否"处理,避免调用方一直等。</summary>
    private Task<bool> ConfirmAsync(string message, string? detail = null, string yes = "是", string no = "否")
    {
        _pendingConfirm?.TrySetResult(false);

        ShowNotice(NoticeKind.Confirm, message, detail, autoHide: false);
        NoticeYes.Content = yes;
        NoticeNo.Content = no;

        _pendingConfirm = new TaskCompletionSource<bool>();
        return _pendingConfirm.Task;
    }

    private void ShowNotice(NoticeKind kind, string message, string? detail, bool autoHide)
    {
        var accent = kind switch
        {
            NoticeKind.Success => OkText,
            NoticeKind.Warning => AmberText,
            NoticeKind.Error => ErrorText,
            _ => (Brush)FindResource("Accent"),
        };

        NoticeText.Text = message;
        NoticeDetail.Text = detail ?? "";
        NoticeDetail.ToolTip = string.IsNullOrEmpty(detail) ? null : detail;
        NoticeDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;

        NoticeBar.BorderBrush = accent;
        NoticeGlyph.Foreground = accent;
        NoticeGlyph.Text = kind switch
        {
            NoticeKind.Success => "\uE73E",
            NoticeKind.Warning or NoticeKind.Error => "\uE7BA",
            NoticeKind.Confirm => "\uE897",
            _ => "\uE946",
        };

        var isConfirm = kind == NoticeKind.Confirm;
        NoticeYes.Visibility = isConfirm ? Visibility.Visible : Visibility.Collapsed;
        NoticeNo.Visibility = isConfirm ? Visibility.Visible : Visibility.Collapsed;
        NoticeClose.Visibility = isConfirm ? Visibility.Collapsed : Visibility.Visible;

        NoticeBar.Visibility = Visibility.Visible;

        _noticeTimer?.Stop();
        if (!autoHide) return;

        // 普通提示 7 秒后自己收起;错误和确认要用户自己关。
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer?.Stop();
            HideNotice();
        };
        _noticeTimer.Start();
    }

    private void HideNotice()
    {
        _noticeTimer?.Stop();
        NoticeBar.Visibility = Visibility.Collapsed;
    }

    private void ResolveConfirm(bool answer)
    {
        var pending = _pendingConfirm;
        _pendingConfirm = null;
        HideNotice();
        pending?.TrySetResult(answer);
    }

    private void NoticeYes_Click(object sender, RoutedEventArgs e) => ResolveConfirm(true);
    private void NoticeNo_Click(object sender, RoutedEventArgs e) => ResolveConfirm(false);
    private void NoticeClose_Click(object sender, RoutedEventArgs e) => ResolveConfirm(false);

    // ---------------- 设置 / 关于 ----------------
    
    private void Settings_Click(object sender, RoutedEventArgs e) => TogglePanel(SettingsPanel);
    private void About_Click(object sender, RoutedEventArgs e) => TogglePanel(AboutPanel);
    
    private void ClosePanel_Click(object sender, RoutedEventArgs e) => HidePanels();
    private void OverlayBackdrop_Click(object sender, MouseButtonEventArgs e) => HidePanels();
    
    /// <summary>再点一次同一个入口就收起来。</summary>
    private void TogglePanel(UIElement panel)
    {
        var show = panel.Visibility != Visibility.Visible;
        HidePanels();
        if (!show) return;
    
        panel.Visibility = Visibility.Visible;
        PanelOverlay.Visibility = Visibility.Visible;
    }
    
    private void HidePanels()
    {
        PanelOverlay.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        AboutPanel.Visibility = Visibility.Collapsed;
    }
    
    private void RepoLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/Kanvin7/StartupInspector") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            NotifyError("打不开链接", ex.Message);
        }
    }
    
    // ---------------- 主题色 ----------------
    
    /// <summary>启动时读取上次选的主题色,顺便把"关于"里的版本号填上。</summary>
    private void ApplyStartupAppearance()
    {
        var saved = AppSettings.LoadAccent();
        var dot = AccentDots().FirstOrDefault(d => string.Equals(d.Tag as string, saved, StringComparison.OrdinalIgnoreCase));
        if (dot is not null) dot.IsChecked = true;   // 会触发 AccentDot_Checked
        else ApplyAccent(saved);
    
        var version = Environment.ProcessPath is { } path
            ? System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion
            : null;
        AboutVersionText.Text = $"版本 {version ?? "未知"} · MIT 许可";
    }
    
    private IEnumerable<RadioButton> AccentDots()
    {
        yield return AccentCyan;
        yield return AccentViolet;
        yield return AccentGreen;
        yield return AccentAmber;
        yield return AccentPink;
        yield return AccentBlue;
    }
    
    private void AccentDot_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string hex } && hex.Length > 0) ApplyAccent(hex);
    }
    
    /// <summary>换主题色:强调色和它的几种派生描边/底色一起改,状态色(绿 / 琥珀 / 红)不动。</summary>
    private void ApplyAccent(string hex)
    {
        Color accent;
        try { accent = (Color)ColorConverter.ConvertFromString(hex); }
        catch { return; }
    
        SetBrush("Accent", accent);
        SetBrush("Line", Color.FromArgb(0x3A, accent.R, accent.G, accent.B));
        SetBrush("LineSoft", Color.FromArgb(0x26, accent.R, accent.G, accent.B));
        SetBrush("AccentDim", Color.FromArgb(0x24, accent.R, accent.G, accent.B));
        SetBrush("Hover", Color.FromArgb(0x12, accent.R, accent.G, accent.B));
        SetBrush("AccentGhost", Color.FromArgb(0x1A, accent.R, accent.G, accent.B));
        SetBrush("AccentMuted", Color.FromArgb(0x3A, accent.R, accent.G, accent.B));
    
        AppSettings.SaveAccent(hex);
    }
    
    /// <summary>
    /// 换主题色时替换资源字典里的那一项,而不是去改画刷的 Color ——
    /// XAML 里声明的画刷会被 WPF 冻结(IsFrozen 为 true),改 Color 会静默失败;
    /// 只要引用方用的是 DynamicResource,替换资源就会立刻生效。
    /// </summary>
    private void SetBrush(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Resources[key] = brush;
    }
    
    // ---------------- 扫描 ----------------

    private async void Rescan()
    {
        ScanButton.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            // 扫描要读注册表 / 计划任务 / 服务,放到后台线程执行,避免界面卡住。
            var result = await Task.Run(() => _scanner.Scan());
            _rows.Clear();
            foreach (var entry in result.Entries)
                _rows.Add(new EntryRow(entry));
            _view.Refresh();
            UpdateStatus(result);
            UpdateActionState();
            _ = LoadIconsAsync(_rows.ToList());
        }
        catch (Exception ex)
        {
            NotifyError("扫描失败", ex.Message);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            ScanButton.IsEnabled = true;
        }
    }

    /// <summary>在后台线程提取每一条目的程序图标,完成后一次性回填到界面。</summary>
    private static async Task LoadIconsAsync(IReadOnlyList<EntryRow> rows)
    {
        var loaded = await Task.Run(() =>
            rows.Select(row => (Row: row, Icon: ShellIcon.Get(row.Entry.ExecutablePath))).ToList());

        foreach (var item in loaded)
            item.Row.Icon = item.Icon;
    }

     private void UpdateStatus(ScanResult result)
     {
         var entries = result.Entries;
         var enabled = entries.Count(e => e.Status == StartupStatus.Enabled);
         var disabled = entries.Count(e => e.Status == StartupStatus.Disabled);
         var elevated = Elevation.IsElevated();
         var slowest = entries.Where(e => e.StartupMilliseconds is not null)
                              .Select(e => e.StartupMilliseconds!.Value)
                              .DefaultIfEmpty(-1)
                              .Max();
 
         TotalText.Text = entries.Count.ToString();
         EnabledText.Text = enabled.ToString();
         DisabledText.Text = disabled.ToString();
         SlowestText.Text = slowest < 0 ? "—" : StartupImpact.Describe(slowest)!;
         ScanTimeText.Text = $"扫描时间 {DateTime.Now:HH:mm:ss}";
         AllCountText.Text = entries.Count.ToString();
         OnCountText.Text = enabled.ToString();
         OffCountText.Text = disabled.ToString();
        
        // 已启用 / 已停用 的构成条,让"大部分是启用还是停用"一眼可见
        var total = enabled + disabled;
        if (total == 0)
        {
            SplitOnColumn.Width = new GridLength(0);
            SplitOffColumn.Width = new GridLength(0);
            SplitText.Text = "还没有条目";
        }
        else
        {
            SplitOnColumn.Width = new GridLength(enabled, GridUnitType.Star);
            SplitOffColumn.Width = new GridLength(disabled, GridUnitType.Star);
            SplitText.Text = $"{enabled * 100 / total}% 处于启用状态";
        }
 
         if (elevated)
         {
            ElevationText.Foreground = (Brush)FindResource("Accent");
             ElevationText.Text = "开机自启项 · 管理员权限";
         }
         else
         {
             ElevationText.Foreground = AmberText;
             ElevationText.Text = "开机自启项 · 普通用户,改系统项需提权";
         }
 
         ElevateButton.Visibility = elevated ? Visibility.Collapsed : Visibility.Visible;
 
         if (result.Warnings.Count == 0)
         {
             WarnText.Visibility = Visibility.Collapsed;
             WarnText.Text = "";
             WarnText.ToolTip = null;
         }
         else
         {
             WarnText.Visibility = Visibility.Visible;
             WarnText.Text = $"⚠ {result.Warnings.Count} 个来源读取失败";
             WarnText.ToolTip = string.Join(Environment.NewLine, result.Warnings);
         }
 
         UpdateFilterSummary();
     }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateActionState();
        UpdateDetail();
    }

    /// <summary>底部那一行:选中单项时显示它的完整命令行和位置。</summary>
    private void UpdateDetail()
    {
        var rows = Grid.SelectedItems.Cast<EntryRow>().ToList();

        if (rows.Count == 0)
        {
            DetailText.Text = "选中一行查看完整命令行";
            DetailText.ToolTip = null;
            DetailLocationText.Visibility = Visibility.Collapsed;
            return;
        }
        
        if (rows.Count > 1)
        {
            DetailText.Text = $"已选 {rows.Count} 项 · 右键可批量启用 / 停用 / 删除";
            DetailText.ToolTip = null;
            DetailLocationText.Visibility = Visibility.Collapsed;
            return;
        }
        
        var entry = rows[0].Entry;
        DetailText.Text = "命令行   " + entry.CommandLine;
        DetailText.ToolTip = entry.CommandLine;
        DetailLocationText.Text = "位置     " + entry.Location;
        DetailLocationText.ToolTip = entry.Location;
        DetailLocationText.Visibility = Visibility.Visible;
    }

    /// <summary>RunOnce 是一次性条目,不受启用/停用控制,选中它时把这两个菜单项灰掉。</summary>
    private void UpdateActionState()
    {
        var rows = Grid.SelectedItems.Cast<EntryRow>().ToList();
        var hasUntoggleable = rows.Any(r => !r.Entry.CanToggle);
        var canToggle = rows.Count > 0 && !hasUntoggleable;

        EnableMenuItem.IsEnabled = canToggle;
        DisableMenuItem.IsEnabled = canToggle;

        var reason = hasUntoggleable ? "RunOnce 是一次性自启项,只能删除" : null;
        EnableMenuItem.ToolTip = reason;
        DisableMenuItem.ToolTip = reason;
    }

    private void ScanButton_Click(object sender, RoutedEventArgs e) => Rescan();

    // ---------------- 过滤 ----------------

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => _view?.Refresh();

    private void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        _view?.Refresh();
        if (FilterSummaryText is not null) UpdateFilterSummary();
    }

    private bool FilterPredicate(object item)
    {
        if (item is not EntryRow row) return false;

        var query = SearchBox?.Text?.Trim() ?? "";
        if (query.Length > 0)
        {
            var haystack = $"{row.Name} {row.Publisher} {row.ExecutablePath} {row.Location}";
            if (!haystack.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                return false;
        }

        if (SourceFilter?.SelectedIndex > 0 && SourceFilter.SelectedItem is string sourceText
            && !string.Equals(sourceText, row.SourceText, StringComparison.Ordinal))
            return false;

        if (_stateFilter != "all")
        {
            var wanted = _stateFilter == "on" ? StartupStatus.Enabled : StartupStatus.Disabled;
            if (row.Entry.Status != wanted) return false;
        }

        return true;
    }

    private void StateFilter_Checked(object sender, RoutedEventArgs e)
    {
        _stateFilter = ReferenceEquals(sender, OnFilter) ? "on"
            : ReferenceEquals(sender, OffFilter) ? "off"
            : "all";

        // 初始选中的那个单选按钮在 InitializeComponent 期间就会触发一次,所以这里都要判空。
        _view?.Refresh();
        if (FilterSummaryText is not null) UpdateFilterSummary();
    }

    private void SortByImpact_Changed(object sender, RoutedEventArgs e)
    {
        _sortByImpact = SortByImpactToggle?.IsChecked == true;
        ApplySort();
    }

    /// <summary>按开机耗时从大到小排;取消勾选就回到默认顺序。</summary>
    private void ApplySort()
    {
        if (_view is null) return;

        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            if (_sortByImpact)
                _view.SortDescriptions.Add(
                    new SortDescription(nameof(EntryRow.ImpactMilliseconds), ListSortDirection.Descending));
        }
    }

    private void UpdateFilterSummary()
    {
        if (FilterSummaryText is null) return;
        
        var shown = _view.Cast<object>().Count();
        FilterSummaryText.Text = $"筛选后 {shown} 条 / 共 {_rows.Count} 条";
        if (EmptyHint is not null)
            EmptyHint.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- 操作 ----------------

    private void Enable_Click(object sender, RoutedEventArgs e) => ApplyToSelection(enable: true);
    private void Disable_Click(object sender, RoutedEventArgs e) => ApplyToSelection(enable: false);

     private async void ApplyToSelection(bool enable)
     {
         var rows = Grid.SelectedItems.Cast<EntryRow>().ToList();
         if (rows.Count == 0)
         {
             NotifyInfo("请先选择条目。");
             return;
         }
 
         var verb = enable ? "启用" : "停用";
         var note = !enable && rows.Any(r => r.Entry.Source == StartupSource.Service)
             ? "注意:停用后的服务不再是自动启动,重新扫描时不会出现在列表里;需要恢复时请用服务管理器。"
             : null;
 
         if (!await ConfirmAsync($"确定要{verb}所选的 {rows.Count} 项吗?", note, verb, "取消")) return;
 
         var failures = new List<string>();
         var succeeded = 0;
         foreach (var row in rows)
         {
             var result = _controller.SetEnabled(row.Entry, enable);
             if (result.Success) succeeded++;
             else failures.Add($"{row.Name}:{result.Message}");
         }
 
         Rescan();
 
         if (failures.Count == 0)
             NotifySuccess($"已{verb} {succeeded} 项");
         else
             NotifyError($"已{verb} {succeeded} 项,{failures.Count} 项失败", string.Join(Environment.NewLine, failures.Take(12)));
     }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var rows = Grid.SelectedItems.Cast<EntryRow>().ToList();
        if (rows.Count == 0)
        {
            NotifyInfo("请先选择条目。");
            return;
        }

        if (!await ConfirmAsync($"确定要删除所选的 {rows.Count} 项吗?",
                "删除前会自动备份,之后可以用\"撤销删除\"还原。服务不会被删除。", "删除", "取消")) return;

         // 先备份再删除,备份文件放在 %LOCALAPPDATA%\\StartupInspector\\backups
         var result = _controller.Delete(rows.Select(r => r.Entry).ToList());
         var backupPath = BackupStore.Save(result.Backup);
         Rescan();

        if (result.Failures.Count == 0)
        {
            NotifySuccess($"已删除 {result.DeletedCount} 项", backupPath is null ? null : "已备份到 " + backupPath);
            return;
        }

        var summary = result.DeletedCount == 0
            ? "删除失败"
            : $"已删除 {result.DeletedCount} 项,{result.Failures.Count} 项未能删除";
        NotifyError(summary, string.Join(Environment.NewLine, result.Failures.Take(12)));
    }

     /// <summary>把最近一次删除的内容建回来。</summary>
     private async void Undo_Click(object sender, RoutedEventArgs e)
     {
         var latest = BackupStore.LoadLatest();
         if (latest is null)
         {
             NotifyInfo("还没有删除记录。");
             return;
         }

         var (backupPath, file) = latest.Value;
         var names = string.Join("\n", file.Items.Take(12).Select(i => "· " + i.DisplayName));
         if (file.Items.Count > 12) names += $"\n… 另有 {file.Items.Count - 12} 项";

        if (!await ConfirmAsync($"还原 {file.CreatedAt} 备份的 {file.Items.Count} 项?", names, "还原", "取消")) return;

         var outcomes = BackupStore.Restore(file);
         Rescan();

         var failed = outcomes.Where(o => !o.Success).ToList();
         if (failed.Count == 0)
         {
             // 整份都还原成功就不再重复提示,文件本身保留作为记录
             BackupStore.MarkRestored(backupPath);
             NotifySuccess($"已还原 {outcomes.Count} 项", "备份文件保留在 " + backupPath);
             return;
         }

        NotifyError($"还原 {outcomes.Count - failed.Count} 项,{failed.Count} 项失败",
            string.Join(Environment.NewLine, failed.Take(12).Select(o => $"{o.Name}:{o.Message}")));
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        var row = Grid.SelectedItem as EntryRow;
        if (row is null) return;

        var path = row.Entry.ExecutablePath;
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                    Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
                else
                    NotifyInfo("找不到对应的文件或目录。");
            }
        }
        catch (Exception ex)
        {
            NotifyError("打开失败", ex.Message);
        }
    }

    private void CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        var row = Grid.SelectedItem as EntryRow;
        if (row is null) return;
        try
        {
            Clipboard.SetText(row.Entry.CommandLine);
        }
        catch
        {
            // 剪贴板偶尔被其他程序占用,忽略
        }
    }

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            NotifyError("提权失败(可能被取消)", ex.Message);
        }
    }
    
    private void CopyLocation_Click(object sender, RoutedEventArgs e)
    {
        var row = Grid.SelectedItem as EntryRow;
        if (row is null) return;
        
        try
        {
            Clipboard.SetText(row.Entry.Location);
        }
        catch
        {
            // 剪贴板偶尔被其他程序占用,忽略
        }
    }

    private void Grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(Grid, e.OriginalSource as DependencyObject) as DataGridRow;
        if (row is not null && !row.IsSelected)
        {
            Grid.SelectedItems.Clear();
            row.IsSelected = true;
        }
    }

    // ---------------- 导出 ----------------

    private void ExportCsv_Click(object sender, RoutedEventArgs e) => Export("csv");
    private void ExportJson_Click(object sender, RoutedEventArgs e) => Export("json");

    private void Export(string kind)
    {
        var entries = _view.Cast<EntryRow>().Select(r => r.Entry).ToList();
        if (entries.Count == 0)
        {
            NotifyInfo("没有可导出的内容。");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"startup-items-{DateTime.Now:yyyyMMdd-HHmmss}.{kind}",
            Filter = kind == "csv" ? "CSV 文件 (*.csv)|*.csv" : "JSON 文件 (*.json)|*.json",
        };
        if (dialog.ShowDialog() != true) return;

        if (kind == "csv") StartupExporter.SaveCsv(dialog.FileName, entries);
        else StartupExporter.SaveJson(dialog.FileName, entries);

        var filtered = entries.Count != _rows.Count ? "(仅当前筛选结果)" : "";
        NotifySuccess($"已导出 {entries.Count} 条{filtered}", dialog.FileName);
    }
}

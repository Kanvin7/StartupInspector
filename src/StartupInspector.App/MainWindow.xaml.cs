using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using StartupInspector.Core;

namespace StartupInspector.App;

public partial class MainWindow : Window
{
    private static readonly Brush PillElevatedBackground = Freeze("#ECFDF5");
    private static readonly Brush PillElevatedText = Freeze("#047857");
    private static readonly Brush PillNormalBackground = Freeze("#FEF3C7");
    private static readonly Brush PillNormalText = Freeze("#92400E");

    private readonly StartupScanner _scanner = new();
    private readonly StartupController _controller = new();
    private readonly ObservableCollection<EntryRow> _rows = new();
    private readonly ICollectionView _view;

    public MainWindow()
    {
        InitializeComponent();

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterPredicate;
        Grid.ItemsSource = _view;

        SourceFilter.Items.Add("全部来源");
        foreach (var source in Enum.GetValues<StartupSource>())
            SourceFilter.Items.Add(StartupLabels.SourceText(source));
        SourceFilter.SelectedIndex = 0;

        StatusFilter.Items.Add("全部状态");
        StatusFilter.Items.Add("已启用");
        StatusFilter.Items.Add("已停用");
        StatusFilter.SelectedIndex = 0;

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

    // ---------------- 扫描 ----------------

    private void Rescan()
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var entries = _scanner.Scan();
            _rows.Clear();
            foreach (var entry in entries)
                _rows.Add(new EntryRow(entry));
            _view.Refresh();
            UpdateStatus(entries);
            _ = LoadIconsAsync(_rows.ToList());
        }
        catch (Exception ex)
        {
            MessageBox.Show("扫描失败:" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
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

    private void UpdateStatus(IReadOnlyList<StartupEntry> entries)
    {
        var enabled = entries.Count(e => e.Status == StartupStatus.Enabled);
        var disabled = entries.Count(e => e.Status == StartupStatus.Disabled);
        var elevated = Elevation.IsElevated();

        StatusText.Text = $"共 {entries.Count} 条   ·   已启用 {enabled}   ·   已停用 {disabled}   ·   扫描时间 {DateTime.Now:HH:mm:ss}";

        if (elevated)
        {
            ElevationPill.Background = PillElevatedBackground;
            ElevationText.Foreground = PillElevatedText;
            ElevationText.Text = "管理员权限";
        }
        else
        {
            ElevationPill.Background = PillNormalBackground;
            ElevationText.Foreground = PillNormalText;
            ElevationText.Text = "普通用户 · 修改系统项需提权";
        }

        ElevateButton.Visibility = elevated ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ScanButton_Click(object sender, RoutedEventArgs e) => Rescan();

    // ---------------- 过滤 ----------------

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => _view?.Refresh();

    private void Filter_Changed(object sender, TextChangedEventArgs e) => _view?.Refresh();

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

        if (StatusFilter?.SelectedIndex > 0 && StatusFilter.SelectedItem is string statusText
            && !string.Equals(statusText, row.StatusText, StringComparison.Ordinal))
            return false;

        return true;
    }

    // ---------------- 操作 ----------------

    private void Enable_Click(object sender, RoutedEventArgs e) => ApplyToSelection(enable: true);
    private void Disable_Click(object sender, RoutedEventArgs e) => ApplyToSelection(enable: false);

    private void ApplyToSelection(bool enable)
    {
        var rows = Grid.SelectedItems.Cast<EntryRow>().ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show("请先选择条目。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var verb = enable ? "启用" : "停用";
        var confirm = MessageBox.Show($"确定要{verb}所选的 {rows.Count} 项吗?", verb,
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        var failures = new List<string>();
        foreach (var row in rows)
        {
            var result = _controller.SetEnabled(row.Entry, enable);
            if (!result.Success) failures.Add($"{row.Name}:{result.Message}");
        }

        Rescan();
        ReportFailures(failures, verb);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var rows = Grid.SelectedItems.Cast<EntryRow>().ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show("请先选择条目。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"确定要删除所选的 {rows.Count} 项吗?此操作不可撤销(服务不会被删除)。",
            "删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        var failures = new List<string>();
        foreach (var row in rows)
        {
            var result = _controller.Delete(row.Entry);
            if (!result.Success) failures.Add($"{row.Name}:{result.Message}");
        }

        Rescan();
        ReportFailures(failures, "删除");
    }

    private static void ReportFailures(List<string> failures, string verb)
    {
        if (failures.Count == 0) return;
        var detail = string.Join("\n", failures.Take(12));
        if (failures.Count > 12) detail += $"\n… 另有 {failures.Count - 12} 项";
        MessageBox.Show($"部分{verb}失败:\n\n{detail}", "结果", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                    MessageBox.Show("找不到对应的文件或目录。", "提示");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开失败:" + ex.Message, "错误");
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
            MessageBox.Show("提权失败(可能被取消):" + ex.Message, "提示");
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
            MessageBox.Show("没有可导出的内容。", "提示");
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

        MessageBox.Show($"已导出 {entries.Count} 条到:\n{dialog.FileName}", "导出完成");
    }
}

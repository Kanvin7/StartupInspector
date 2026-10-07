using System.ComponentModel;
using System.Windows.Media;
using StartupInspector.Core;

namespace StartupInspector.App;

/// <summary>界面行:包装一条 StartupEntry,提供用于绑定的展示字段(含图标与配色)。</summary>
public sealed class EntryRow : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public EntryRow(StartupEntry entry) => Entry = entry;

    public StartupEntry Entry { get; }

    /// <summary>程序图标,扫描结束后在后台线程填充。</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    public string Name => Entry.Name;
    public string SourceText => StartupLabels.SourceText(Entry.Source);
    public string StatusText => StartupLabels.StatusText(Entry.Status);
    public string Publisher => string.IsNullOrEmpty(Entry.Publisher) ? "—" : Entry.Publisher;
    public string ExecutablePath => string.IsNullOrEmpty(Entry.ExecutablePath) ? "(未知)" : Entry.ExecutablePath;
    public string Location => Entry.Location;
    public string CommandLine => Entry.CommandLine;
    public bool IsDisabled => Entry.Status == StartupStatus.Disabled;
    public bool IsOrphaned => Entry.IsOrphaned;

    public Brush SourceBrush => Entry.Source switch
    {
        StartupSource.RegistryRunCurrentUser or StartupSource.RegistryRunLocalMachine => Palette.Registry,
        StartupSource.StartupFolderCurrentUser or StartupSource.StartupFolderAllUsers => Palette.Folder,
        StartupSource.ScheduledTask => Palette.Task,
        StartupSource.Service => Palette.Service,
        _ => Palette.Muted,
    };

    public Brush StatusBrush => Entry.Status == StartupStatus.Enabled ? Palette.Enabled : Palette.Muted;

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal static class Palette
{
    public static readonly Brush Registry = Make("#A78BFA");
    public static readonly Brush Folder = Make("#22D3EE");
    public static readonly Brush Task = Make("#FBBF24");
    public static readonly Brush Service = Make("#60A5FA");
    public static readonly Brush Enabled = Make("#4ADE80");
    public static readonly Brush Muted = Make("#8A8A8A");

    private static Brush Make(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

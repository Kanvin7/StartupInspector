using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace StartupInspector.Core;

/// <summary>自启项的来源。不同来源的"启用/停用"机制不同。</summary>
public enum StartupSource
{
    RegistryRunCurrentUser,
    RegistryRunOnceCurrentUser,
    RegistryRunLocalMachine,
    RegistryRunOnceLocalMachine,
    StartupFolderCurrentUser,
    StartupFolderAllUsers,
    ScheduledTask,
    PackagedAppStartupTask,
    Service
}

/// <summary>当前运行状态。</summary>
public enum StartupStatus
{
    Enabled,
    Disabled,
    Unknown
}

/// <summary>
/// 一条自启记录。公开属性会在界面展示并可导出;
/// 标注 [JsonIgnore] 的成员是执行启停/删除所需的内部信息,不参与导出。
/// </summary>
public sealed class StartupEntry
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public StartupSource Source { get; init; }
    public StartupStatus Status { get; init; } = StartupStatus.Unknown;

    public string CommandLine { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Description { get; init; } = "";
    public string Location { get; init; } = "";

    /// <summary>修改该项是否需要管理员权限。</summary>
    public bool RequiresElevation { get; init; }

    /// <summary>目标可执行文件已不存在(残留项)。</summary>
    public bool IsOrphaned { get; init; }

    /// <summary>
    /// RunOnce 是一次性自启项:命令执行后条目会被 Windows 自行删除,
    /// 也不受 StartupApproved 开关控制,因此只能删除、不能"停用"。
    /// </summary>
    [JsonIgnore]
    public bool IsRunOnce => Source is StartupSource.RegistryRunOnceCurrentUser or StartupSource.RegistryRunOnceLocalMachine;

    /// <summary>该条目是否支持"启用 / 停用"。</summary>
    [JsonIgnore]
    public bool CanToggle => !IsRunOnce;

    /// <summary>
    /// 最近一次开机这一项花了多少毫秒(来自开机性能日志,读不到时为 null)。
    /// 耗时要在所有来源扫完之后统一回填,所以这个是可写的。
    /// </summary>
    public int? StartupMilliseconds { get; set; }

    [JsonIgnore] internal RegistryHive Hive { get; init; }
    [JsonIgnore] internal RegistryView View { get; init; }
    [JsonIgnore] internal string? RegistryPath { get; init; }
    [JsonIgnore] internal string? RegistryValueName { get; init; }
    [JsonIgnore] internal string? FileFullPath { get; init; }
    [JsonIgnore] internal string? TaskPath { get; init; }
    [JsonIgnore] internal string? ServiceName { get; init; }

    /// <summary>打包应用(Store/MSIX)的包族名,例如 Microsoft.PowerAutomateDesktop_8wekyb3d8bbwe。</summary>
    [JsonIgnore] internal string? PackageFamilyName { get; init; }

    /// <summary>打包应用的启动任务 Id,例如 AutoStartTask。</summary>
    [JsonIgnore] internal string? PackageTaskId { get; init; }

    /// <summary>服务是否为"自动(延迟启动)"。</summary>
    [JsonIgnore] internal bool IsDelayedAutoStart { get; init; }

    /// <summary>条目来自启动文件夹下的 Disabled 子目录。</summary>
    [JsonIgnore] internal bool InDisabledSubfolder { get; init; }
}

/// <summary>一次扫描的结果:条目,以及读取失败的来源说明。</summary>
public sealed class ScanResult
{
    public IReadOnlyList<StartupEntry> Entries { get; init; } = Array.Empty<StartupEntry>();

    /// <summary>某个来源整体读取失败时的说明;个别条目失败不计入。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>启用/停用/删除的结果。</summary>
public sealed class ControlResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }

    public static ControlResult Ok(string? message = null) => new() { Success = true, Message = message };
    public static ControlResult Fail(string message) => new() { Success = false, Message = message };
}

/// <summary>界面/命令行共用的中文标签。</summary>
public static class StartupLabels
{
    public static string SourceText(StartupSource source) => source switch
    {
        StartupSource.RegistryRunCurrentUser => "注册表 Run (当前用户)",
        StartupSource.RegistryRunOnceCurrentUser => "注册表 RunOnce (当前用户)",
        StartupSource.RegistryRunLocalMachine => "注册表 Run (本机)",
        StartupSource.RegistryRunOnceLocalMachine => "注册表 RunOnce (本机)",
        StartupSource.StartupFolderCurrentUser => "启动文件夹 (当前用户)",
        StartupSource.StartupFolderAllUsers => "启动文件夹 (所有用户)",
        StartupSource.ScheduledTask => "计划任务",
        StartupSource.PackagedAppStartupTask => "应用启动任务 (当前用户)",
        StartupSource.Service => "系统服务",
        _ => source.ToString()
    };

    public static string StatusText(StartupStatus status) => status switch
    {
        StartupStatus.Enabled => "已启用",
        StartupStatus.Disabled => "已停用",
        _ => "未知"
    };
}

/// <summary>权限判断。</summary>
public static class Elevation
{
    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}

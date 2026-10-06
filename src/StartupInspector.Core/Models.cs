using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace StartupInspector.Core;

/// <summary>自启项的来源。不同来源的"启用/停用"机制不同。</summary>
public enum StartupSource
{
    RegistryRunCurrentUser,
    RegistryRunLocalMachine,
    StartupFolderCurrentUser,
    StartupFolderAllUsers,
    ScheduledTask,
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

    [JsonIgnore] internal RegistryHive Hive { get; init; }
    [JsonIgnore] internal RegistryView View { get; init; }
    [JsonIgnore] internal string? RegistryPath { get; init; }
    [JsonIgnore] internal string? RegistryValueName { get; init; }
    [JsonIgnore] internal string? FileFullPath { get; init; }
    [JsonIgnore] internal string? TaskPath { get; init; }
    [JsonIgnore] internal string? ServiceName { get; init; }
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
        StartupSource.RegistryRunLocalMachine => "注册表 Run (本机)",
        StartupSource.StartupFolderCurrentUser => "启动文件夹 (当前用户)",
        StartupSource.StartupFolderAllUsers => "启动文件夹 (所有用户)",
        StartupSource.ScheduledTask => "计划任务",
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

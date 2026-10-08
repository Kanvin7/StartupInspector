using System.Diagnostics;
using Microsoft.Win32;
using TaskSched = Microsoft.Win32.TaskScheduler;

namespace StartupInspector.Core;

/// <summary>
/// 对自启项执行"启用 / 停用 / 删除"。停用一律采用 Windows 自己的机制:
///   注册表 Run —— 写入 Explorer\StartupApproved\Run(或 Run32)的启用/禁用标记,与任务管理器完全一致
///   注册表 RunOnce —— 不支持停用(该机制不作用于 RunOnce),只能删除
///   启动文件夹 —— 写入 Explorer\StartupApproved\StartupFolder 的标记(文件保留在原处)
///   计划任务 —— 修改 Enabled 标志后重新注册
///   应用启动任务 —— 写入打包应用 AppModel 下的 State 值(用户自己的 HKCU,无需管理员)
///   服务    —— 通过 sc config 修改启动类型(需要管理员)
/// 注意:停用不会改动注册表里的启动项本身,程序下次运行时若重建启动项,需要重新停用。
/// </summary>
public sealed class StartupController
{
    public ControlResult SetEnabled(StartupEntry entry, bool enable) => entry.Source switch
    {
        StartupSource.RegistryRunCurrentUser or StartupSource.RegistryRunLocalMachine => ToggleRegistry(entry, enable),
        StartupSource.RegistryRunOnceCurrentUser or StartupSource.RegistryRunOnceLocalMachine =>
            ControlResult.Fail("RunOnce 是一次性自启项,不受启用/停用控制,只能删除或等它自行执行"),
        StartupSource.StartupFolderCurrentUser or StartupSource.StartupFolderAllUsers => ToggleStartupFolder(entry, enable),
        StartupSource.PackagedAppStartupTask => PackagedApps.SetEnabled(entry, enable),
        StartupSource.ScheduledTask => ToggleTask(entry, enable),
        StartupSource.Service => ToggleService(entry, enable),
        _ => ControlResult.Fail("不支持的来源"),
    };

    public ControlResult Delete(StartupEntry entry) => entry.Source switch
    {
        StartupSource.RegistryRunCurrentUser or StartupSource.RegistryRunLocalMachine
            or StartupSource.RegistryRunOnceCurrentUser or StartupSource.RegistryRunOnceLocalMachine => DeleteRegistry(entry),
        StartupSource.StartupFolderCurrentUser or StartupSource.StartupFolderAllUsers => DeleteStartupFolder(entry),
        StartupSource.PackagedAppStartupTask =>
            ControlResult.Fail("打包应用的启动任务只能停用、不能删除(任务管理器也一样)"),
        StartupSource.ScheduledTask => DeleteTask(entry),
        StartupSource.Service => ControlResult.Fail("为避免误伤系统,本工具不删除服务(可先停用)。"),
        _ => ControlResult.Fail("不支持的来源"),
    };

    /// <summary>
    /// 批量删除:每删掉一项之前先抓一份原始信息,之后可以用"撤销上次删除"还原。
    /// </summary>
    public DeleteResult Delete(IReadOnlyList<StartupEntry> entries)
    {
        var backups = new List<BackupItem>();
        var failures = new List<string>();
        var deleted = 0;

        foreach (var entry in entries)
        {
            // 先备份后删除,顺序不能反
            var captured = BackupStore.Capture(entry);
            var result = Delete(entry);

            if (!result.Success)
            {
                failures.Add($"{entry.Name}:{result.Message}");
                continue;
            }

            deleted++;
            if (captured is not null) backups.Add(captured);
        }

        return new DeleteResult { DeletedCount = deleted, Failures = failures, Backup = backups };
    }

    // ---------------- 注册表 ----------------

    private static ControlResult ToggleRegistry(StartupEntry entry, bool enable)
    {
        if (entry.RegistryPath is null || entry.RegistryValueName is null)
            return ControlResult.Fail("缺少注册表信息");

        try
        {
            var is32Bit = entry.View == RegistryView.Registry32;
            var stored = entry.RegistryValueName;
            var plainName = stored.StartsWith("!", StringComparison.Ordinal) ? stored[1..] : stored;

            using var baseKey = RegistryKey.OpenBaseKey(entry.Hive, entry.View);
            using var key = baseKey.OpenSubKey(entry.RegistryPath, writable: true);
            if (key is null)
                return ControlResult.Fail(entry.Hive == RegistryHive.LocalMachine
                    ? "需要管理员权限(HKLM)"
                    : "注册表项不存在");

            // 兼容早期版本:若值名曾被加上 "!" 前缀,先还原为正常名称。
            if (!string.Equals(stored, plainName, StringComparison.Ordinal) && HasValue(key, stored))
            {
                if (!HasValue(key, plainName))
                {
                    var value = key.GetValue(stored);
                    var kind = key.GetValueKind(stored);
                    key.SetValue(plainName, value!, kind);
                }
                key.DeleteValue(stored, false);
            }
            else if (!HasValue(key, plainName))
            {
                return ControlResult.Fail("找不到该项");
            }

            StartupApproval.SetRunDisabled(entry.Hive, is32Bit, plainName, !enable);
            return ControlResult.Ok(enable ? "已启用" : "已停用");
        }
        catch (UnauthorizedAccessException)
        {
            return ControlResult.Fail("访问被拒绝,需要以管理员身份运行");
        }
        catch (Exception ex)
        {
            return ControlResult.Fail(ex.Message);
        }
    }

    private static ControlResult DeleteRegistry(StartupEntry entry)
    {
        if (entry.RegistryPath is null || entry.RegistryValueName is null)
            return ControlResult.Fail("缺少注册表信息");

        try
        {
            var is32Bit = entry.View == RegistryView.Registry32;
            var stored = entry.RegistryValueName;
            var plainName = stored.StartsWith("!", StringComparison.Ordinal) ? stored[1..] : stored;

            using var baseKey = RegistryKey.OpenBaseKey(entry.Hive, entry.View);
            using var key = baseKey.OpenSubKey(entry.RegistryPath, writable: true);
            if (key is null) return ControlResult.Fail("注册表项不存在或权限不足");

            var removed = false;
            if (HasValue(key, stored)) { key.DeleteValue(stored, false); removed = true; }
            if (!string.Equals(plainName, stored, StringComparison.Ordinal) && HasValue(key, plainName))
            {
                key.DeleteValue(plainName, false);
                removed = true;
            }
            if (!removed) return ControlResult.Fail("找不到该项");

            StartupApproval.RemoveRun(entry.Hive, is32Bit, plainName);
            return ControlResult.Ok("已删除");
        }
        catch (UnauthorizedAccessException)
        {
            return ControlResult.Fail("访问被拒绝,需要以管理员身份运行");
        }
        catch (Exception ex)
        {
            return ControlResult.Fail(ex.Message);
        }
    }

    private static bool HasValue(RegistryKey key, string name)
        => key.GetValueNames().Any(n => string.Equals(n, name, StringComparison.Ordinal));

    // ---------------- 启动文件夹 ----------------

    private static ControlResult ToggleStartupFolder(StartupEntry entry, bool enable)
    {
        if (entry.FileFullPath is null) return ControlResult.Fail("缺少文件信息");

        try
        {
            var file = entry.FileFullPath;
            if (!File.Exists(file)) return ControlResult.Fail("找不到该项文件");

            var hive = entry.Source == StartupSource.StartupFolderAllUsers
                ? RegistryHive.LocalMachine
                : RegistryHive.CurrentUser;

            // 条目本来就在 Disabled 子目录里时,只写标志不会让它重新生效,必须先把文件移回上级目录。
            if (enable && entry.InDisabledSubfolder)
            {
                var disabledDirectory = Path.GetDirectoryName(file);
                var startupDirectory = disabledDirectory is null ? null : Path.GetDirectoryName(disabledDirectory);
                if (startupDirectory is null) return ControlResult.Fail("找不到上级启动文件夹");

                var target = Path.Combine(startupDirectory, Path.GetFileName(file));
                if (File.Exists(target)) return ControlResult.Fail($"启动文件夹中已存在同名文件,未移动:{target}");
                File.Move(file, target);
            }

            StartupApproval.SetFolderDisabled(hive, Path.GetFileName(file), !enable);
            return ControlResult.Ok(enable ? "已启用" : "已停用");
        }
        catch (UnauthorizedAccessException)
        {
            return ControlResult.Fail("访问被拒绝,需要以管理员身份运行");
        }
        catch (Exception ex)
        {
            return ControlResult.Fail(ex.Message);
        }
    }

    private static ControlResult DeleteStartupFolder(StartupEntry entry)
    {
        if (entry.FileFullPath is null) return ControlResult.Fail("缺少文件信息");
        try
        {
            var file = entry.FileFullPath;
            if (!File.Exists(file)) return ControlResult.Fail("文件不存在");

            var hive = entry.Source == StartupSource.StartupFolderAllUsers
                ? RegistryHive.LocalMachine
                : RegistryHive.CurrentUser;

            StartupApproval.RemoveFolder(hive, Path.GetFileName(file));
            File.Delete(file);
            return ControlResult.Ok("已删除");
        }
        catch (Exception ex)
        {
            return ControlResult.Fail(ex.Message);
        }
    }

    // ---------------- 计划任务 ----------------

    private static ControlResult ToggleTask(StartupEntry entry, bool enable)
    {
        if (entry.TaskPath is null) return ControlResult.Fail("缺少计划任务信息");
        try
        {
            using var service = new TaskSched.TaskService();
            var task = service.GetTask(entry.TaskPath);
            if (task is null) return ControlResult.Fail("找不到计划任务(或权限不足)");

            task.Enabled = enable;
            task.RegisterChanges();
            return ControlResult.Ok(enable ? "已启用" : "已停用");
        }
        catch (UnauthorizedAccessException)
        {
            return ControlResult.Fail("访问被拒绝,需要以管理员身份运行");
        }
        catch (Exception ex)
        {
            return ControlResult.Fail(ex.Message);
        }
    }

    private static ControlResult DeleteTask(StartupEntry entry)
    {
        if (entry.TaskPath is null) return ControlResult.Fail("缺少计划任务信息");
        try
        {
            using var service = new TaskSched.TaskService();
            service.RootFolder.DeleteTask(entry.TaskPath, false);
            return ControlResult.Ok("已删除");
        }
        catch (Exception ex)
        {
            return ControlResult.Fail(ex.Message);
        }
    }

    // ---------------- 服务 ----------------

    private static ControlResult ToggleService(StartupEntry entry, bool enable)
    {
        if (entry.ServiceName is null) return ControlResult.Fail("缺少服务名");

        try
        {
            // 原本是"自动(延迟启动)"的服务,重新启用时要保留延迟启动。
            var start = enable
                ? (entry.IsDelayedAutoStart ? "delayed-auto" : "auto")
                : "disabled";
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"config \"{entry.ServiceName}\" start= {start}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(psi);
            if (process is null) return ControlResult.Fail("无法启动 sc.exe");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode == 0) return ControlResult.Ok(enable ? "已设为自动启动" : "已停用");

            var message = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return ControlResult.Fail(message.Trim());
        }
        catch (Exception ex)
        {
            return ControlResult.Fail(ex.Message);
        }
    }
}

/// <summary>批量删除的结果:删掉了几项、备份内容、失败原因。</summary>
public sealed class DeleteResult
{
    public int DeletedCount { get; init; }
    public IReadOnlyList<BackupItem> Backup { get; init; } = Array.Empty<BackupItem>();
    public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();
}

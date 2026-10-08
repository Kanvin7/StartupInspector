
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using TaskSched = Microsoft.Win32.TaskScheduler;

namespace StartupInspector.Core;

/// <summary>删除前的备份:足以把这一项重新建回来。</summary>
public sealed class BackupItem
{
    /// <summary>registryRun / startupFile / scheduledTask。</summary>
    public string Kind { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Source { get; set; } = "";

    /// <summary>删除前是否处于"已停用"状态。</summary>
    public bool WasDisabled { get; set; }

    // 注册表 Run / RunOnce
    public string? Hive { get; set; }
    public string? View { get; set; }
    public string? SubKey { get; set; }
    public string? ValueName { get; set; }
    public string? ValueKind { get; set; }
    public string? Value { get; set; }

    // 启动文件夹
    public string? FilePath { get; set; }
    public string? FileBytes { get; set; }

    // 计划任务
    public string? TaskPath { get; set; }
    public string? TaskXml { get; set; }
}

/// <summary>一个备份文件的内容。</summary>
public sealed class BackupFile
{
    public string CreatedAt { get; set; } = "";
    public string Machine { get; set; } = "";
    public List<BackupItem> Items { get; set; } = new();
}

/// <summary>单项还原的结果。</summary>
public sealed class RestoreOutcome
{
    public string Name { get; init; } = "";
    public bool Success { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>
/// 删除备份的落盘与还原。文件放在
/// %LOCALAPPDATA%\StartupInspector\backups\delete-&lt;时间&gt;.json,
/// 界面上用"撤销上次删除"把最近一次删掉的内容建回来。
/// </summary>
public static class BackupStore
{
    private const int KeepFiles = 40;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StartupInspector",
        "backups");

    /// <summary>保存一次删除的备份,返回文件路径;没有可备份内容时返回 null。</summary>
    public static string? Save(IReadOnlyList<BackupItem> items)
    {
        if (items.Count == 0) return null;
        try
        {
            Directory.CreateDirectory(Folder);

            var file = new BackupFile
            {
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Machine = Environment.MachineName,
                Items = items.ToList(),
            };

            var path = Path.Combine(Folder, $"delete-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOptions), new UTF8Encoding(false));
            Prune();
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>最近一次删除的备份,没有则返回 null。</summary>
    public static (string Path, BackupFile File)? LoadLatest()
    {
        try
        {
            if (!Directory.Exists(Folder)) return null;

            var newest = Directory.GetFiles(Folder, "delete-*.json")
                .Where(p => !p.Contains(".restored."))
                .OrderByDescending(p => p, StringComparer.Ordinal)
                .FirstOrDefault();
            if (newest is null) return null;

            var file = JsonSerializer.Deserialize<BackupFile>(File.ReadAllText(newest));
            return file is null ? null : (newest, file);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>按备份把条目建回来。</summary>
    public static IReadOnlyList<RestoreOutcome> Restore(BackupFile file)
    {
        var results = new List<RestoreOutcome>();
        foreach (var item in file.Items) results.Add(RestoreOne(item));
        return results;
    }

    /// <summary>在删除前抓取这一项的原始信息,抓不到时返回 null(仍会照常删除,只是无法还原)。</summary>
    internal static BackupItem? Capture(StartupEntry entry)
    {
        var item = new BackupItem
        {
            DisplayName = entry.Name,
            Source = entry.Source.ToString(),
            WasDisabled = entry.Status == StartupStatus.Disabled,
        };

        switch (entry.Source)
        {
            case StartupSource.RegistryRunCurrentUser:
            case StartupSource.RegistryRunOnceCurrentUser:
            case StartupSource.RegistryRunLocalMachine:
            case StartupSource.RegistryRunOnceLocalMachine:
            {
                if (entry.RegistryPath is null || entry.RegistryValueName is null) return null;
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(entry.Hive, entry.View);
                    using var key = baseKey.OpenSubKey(entry.RegistryPath, writable: false);
                    if (key is null) return null;

                    var kind = key.GetValueKind(entry.RegistryValueName);
                    var value = key.GetValue(entry.RegistryValueName);

                    item.Kind = "registryRun";
                    item.Hive = entry.Hive.ToString();
                    item.View = entry.View.ToString();
                    item.SubKey = entry.RegistryPath;
                    item.ValueName = entry.RegistryValueName;
                    item.ValueKind = kind.ToString();
                    item.Value = kind == RegistryValueKind.Binary && value is byte[] bytes
                        ? Convert.ToBase64String(bytes)
                        : value?.ToString() ?? "";
                    return item;
                }
                catch
                {
                    return null;
                }
            }

            case StartupSource.StartupFolderCurrentUser:
            case StartupSource.StartupFolderAllUsers:
            {
                if (entry.FileFullPath is null || !File.Exists(entry.FileFullPath)) return null;
                try
                {
                    item.Kind = "startupFile";
                    item.FilePath = entry.FileFullPath;
                    item.FileBytes = Convert.ToBase64String(File.ReadAllBytes(entry.FileFullPath));
                    return item;
                }
                catch
                {
                    return null;
                }
            }

            case StartupSource.ScheduledTask:
            {
                if (entry.TaskPath is null) return null;
                try
                {
                    using var service = new TaskSched.TaskService();
                    var task = service.GetTask(entry.TaskPath);
                    if (task is null) return null;

                    item.Kind = "scheduledTask";
                    item.TaskPath = entry.TaskPath;
                    item.TaskXml = task.Xml;
                    return item;
                }
                catch
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static RestoreOutcome RestoreOne(BackupItem item)
    {
        try
        {
            switch (item.Kind)
            {
                case "registryRun":
                {
                    if (item.Hive is null || item.View is null || item.SubKey is null || item.ValueName is null)
                        return Fail(item, "备份信息不完整");

                    var hive = Enum.Parse<RegistryHive>(item.Hive);
                    var view = Enum.Parse<RegistryView>(item.View);
                    var kind = item.ValueKind is null
                        ? RegistryValueKind.String
                        : Enum.Parse<RegistryValueKind>(item.ValueKind);

                    object value = kind == RegistryValueKind.Binary && item.Value is not null
                        ? Convert.FromBase64String(item.Value)
                        : item.Value ?? "";

                    using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                    using (var key = baseKey.CreateSubKey(item.SubKey, writable: true))
                    {
                        if (key is null) return Fail(item, "无法打开注册表项");
                        key.SetValue(item.ValueName, value, kind);
                    }

                    StartupApproval.SetRunDisabled(hive, view == RegistryView.Registry32, item.ValueName, item.WasDisabled);
                    return Ok(item);
                }

                case "startupFile":
                {
                    if (item.FilePath is null || item.FileBytes is null) return Fail(item, "备份信息不完整");
                    if (File.Exists(item.FilePath)) return Fail(item, "原位置已有同名文件,未覆盖");

                    var directory = Path.GetDirectoryName(item.FilePath);
                    if (directory is not null) Directory.CreateDirectory(directory);
                    File.WriteAllBytes(item.FilePath, Convert.FromBase64String(item.FileBytes));

                    var hive = item.Source == StartupSource.StartupFolderAllUsers.ToString()
                        ? RegistryHive.LocalMachine
                        : RegistryHive.CurrentUser;
                    StartupApproval.SetFolderDisabled(hive, Path.GetFileName(item.FilePath), item.WasDisabled);
                    return Ok(item);
                }

                case "scheduledTask":
                {
                    if (item.TaskPath is null || item.TaskXml is null) return Fail(item, "备份信息不完整");

                    using var service = new TaskSched.TaskService();
                    if (service.GetTask(item.TaskPath) is not null) return Fail(item, "同名计划任务已存在,未覆盖");
                    service.RootFolder.RegisterTask(item.TaskPath, item.TaskXml, TaskSched.TaskCreation.Create);
                    return Ok(item);
                }
            }

            return Fail(item, "未知的备份类型");
        }
        catch (Exception ex)
        {
            return Fail(item, ex.Message);
        }
    }

    /// <summary>整份备份都还原成功后打个标记,免得"撤销上次删除"重复提示同一份(文件保留作记录)。</summary>
    public static void MarkRestored(string path)
    {
        try
        {
            var target = Path.ChangeExtension(path, null) + ".restored.json";
            if (File.Exists(path) && !File.Exists(target)) File.Move(path, target);
        }
        catch
        {
            // 标记失败不影响还原结果
        }
    }

    private static void Prune()
    {
        try
        {
            var stale = Directory.GetFiles(Folder, "delete-*.json")
                .OrderByDescending(p => p, StringComparer.Ordinal)
                .Skip(KeepFiles);
            foreach (var path in stale) File.Delete(path);
        }
        catch
        {
            // 清理失败不影响本次备份
        }
    }

    private static RestoreOutcome Ok(BackupItem item)
        => new() { Name = item.DisplayName, Success = true, Message = "已还原" };

    private static RestoreOutcome Fail(BackupItem item, string message)
        => new() { Name = item.DisplayName, Success = false, Message = message };
}

using System.ServiceProcess;
using Microsoft.Win32;
using TaskSched = Microsoft.Win32.TaskScheduler;

namespace StartupInspector.Core;

/// <summary>
/// 扫描 Windows 上常见的开机自启位置:注册表 Run/RunOnce(含 32/64 位视图)、
/// 启动文件夹、带"登录/开机"触发器的计划任务、自动启动的服务。
/// 每个来源独立捕获异常,单个来源失败不会清空整个结果。
/// </summary>
public sealed class StartupScanner
{
    public IReadOnlyList<StartupEntry> Scan()
    {
        var items = new List<StartupEntry>();
        TryAdd(items, ScanRegistryRun);
        TryAdd(items, ScanStartupFolders);
        TryAdd(items, ScanScheduledTasks);
        TryAdd(items, ScanServices);

        return items
            .OrderBy(i => i.Source)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void TryAdd(List<StartupEntry> sink, Func<IEnumerable<StartupEntry>> producer)
    {
        try { sink.AddRange(producer()); }
        catch { /* 单个来源失败不应影响其他来源 */ }
    }

    // ================= 注册表 Run / RunOnce =================

    private static readonly (RegistryHive Hive, RegistryView View, string SubKey, StartupSource Source)[] RunKeys =
    {
        (RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Run", StartupSource.RegistryRunCurrentUser),
        (RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", StartupSource.RegistryRunCurrentUser),
        (RegistryHive.CurrentUser, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Run", StartupSource.RegistryRunCurrentUser),
        (RegistryHive.CurrentUser, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", StartupSource.RegistryRunCurrentUser),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Run", StartupSource.RegistryRunLocalMachine),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", StartupSource.RegistryRunLocalMachine),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Run", StartupSource.RegistryRunLocalMachine),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", StartupSource.RegistryRunLocalMachine),
    };

    private static IEnumerable<StartupEntry> ScanRegistryRun()
    {
        var output = new List<StartupEntry>();
        foreach (var loc in RunKeys)
        {
            using var baseKey = RegistryKey.OpenBaseKey(loc.Hive, loc.View);
            using var key = baseKey.OpenSubKey(loc.SubKey, writable: false);
            if (key is null) continue;

            foreach (var rawName in key.GetValueNames())
            {
                if (string.IsNullOrEmpty(rawName)) continue;
                var raw = key.GetValue(rawName)?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(raw)) continue;

                // 真实机制:任务管理器把"已禁用"记录在 Explorer\StartupApproved 下;
                // 同时兼容早期版本可能留下的 "!" 前缀。
                var legacyBang = rawName.StartsWith("!", StringComparison.Ordinal);
                var name = legacyBang ? rawName[1..] : rawName;
                var disabled = legacyBang || StartupApproval.IsRunDisabled(loc.Hive, loc.View == RegistryView.Registry32, name);
                var (exe, args) = CommandLine.Parse(raw);
                var viewText = loc.View == RegistryView.Registry32 ? " (32 位视图)" : "";

                output.Add(new StartupEntry
                {
                    Id = $"reg|{loc.Hive}|{loc.View}|{loc.SubKey}|{name}",
                    Name = name,
                    Source = loc.Source,
                    Status = disabled ? StartupStatus.Disabled : StartupStatus.Enabled,
                    CommandLine = raw,
                    ExecutablePath = exe,
                    Arguments = args,
                    Publisher = FileInfoHelper.Publisher(exe),
                    Description = FileInfoHelper.Description(exe),
                    Location = $"{(loc.Hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM")}\\{loc.SubKey}{viewText}",
                    RequiresElevation = loc.Hive == RegistryHive.LocalMachine,
                    IsOrphaned = FileInfoHelper.IsOrphaned(exe),
                    Hive = loc.Hive,
                    View = loc.View,
                    RegistryPath = loc.SubKey,
                    RegistryValueName = rawName,
                });
            }
        }
        return output;
    }

    // ================= 启动文件夹 =================

    private static IEnumerable<StartupEntry> ScanStartupFolders()
    {
        var output = new List<StartupEntry>();
        var folders = new (string Path, StartupSource Source, bool Elevation)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.StartupFolderCurrentUser, false),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.StartupFolderAllUsers, true),
        };

        foreach (var folder in folders)
        {
            if (string.IsNullOrEmpty(folder.Path) || !Directory.Exists(folder.Path)) continue;

            output.AddRange(EnumerateFolder(folder.Path, folder.Source, folder.Elevation, inDisabledSubfolder: false));
            var disabledDir = Path.Combine(folder.Path, "Disabled");
            if (Directory.Exists(disabledDir))
                output.AddRange(EnumerateFolder(disabledDir, folder.Source, folder.Elevation, inDisabledSubfolder: true));
        }
        return output;
    }

    private static IEnumerable<StartupEntry> EnumerateFolder(string dir, StartupSource source, bool elevation, bool inDisabledSubfolder)
    {
        var hive = source == StartupSource.StartupFolderAllUsers ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var fileName = Path.GetFileName(file);
            if (string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            if (fileName.StartsWith("~$", StringComparison.Ordinal)) continue;

            var target = ShortcutResolver.Resolve(file);
            var exe = string.IsNullOrWhiteSpace(target.ExecutablePath) ? file : target.ExecutablePath;
            var args = target.Arguments;
            var disabled = inDisabledSubfolder || StartupApproval.IsFolderDisabled(hive, fileName);

            yield return new StartupEntry
            {
                Id = $"file|{file}",
                Name = Path.GetFileNameWithoutExtension(fileName),
                Source = source,
                Status = disabled ? StartupStatus.Disabled : StartupStatus.Enabled,
                CommandLine = args.Length > 0 ? $"\"{exe}\" {args}" : $"\"{exe}\"",
                ExecutablePath = exe,
                Arguments = args,
                Publisher = FileInfoHelper.Publisher(exe),
                Description = FileInfoHelper.Description(exe),
                Location = dir,
                RequiresElevation = elevation,
                IsOrphaned = FileInfoHelper.IsOrphaned(exe),
                FileFullPath = file,
            };
        }
    }

    // ================= 计划任务(仅登录/开机触发) =================

    private static IEnumerable<StartupEntry> ScanScheduledTasks()
    {
        var output = new List<StartupEntry>();
        using var service = new TaskSched.TaskService();

        WalkFolder(service.RootFolder, output);
        return output;
    }

    private static void WalkFolder(TaskSched.TaskFolder folder, List<StartupEntry> output)
    {
        try
        {
        foreach (var task in folder.GetTasks())
        {
            try
            {
                var definition = task.Definition;
                var isLogonOrBoot = definition.Triggers.Any(t =>
                    t.TriggerType == TaskSched.TaskTriggerType.Logon ||
                    t.TriggerType == TaskSched.TaskTriggerType.Boot);
                if (!isLogonOrBoot) continue;

                var exec = definition.Actions.OfType<TaskSched.ExecAction>().FirstOrDefault();
                var exe = exec?.Path ?? "";
                var args = exec?.Arguments ?? "";

                output.Add(new StartupEntry
                {
                    Id = $"task|{task.Path}",
                    Name = task.Name,
                    Source = StartupSource.ScheduledTask,
                    Status = task.Enabled ? StartupStatus.Enabled : StartupStatus.Disabled,
                    CommandLine = string.IsNullOrEmpty(args) ? exe : $"\"{exe}\" {args}",
                    ExecutablePath = exe,
                    Arguments = args,
                    Publisher = FileInfoHelper.Publisher(exe),
                    Description = FileInfoHelper.Description(exe),
                    Location = task.Path,
                    RequiresElevation = true,
                    IsOrphaned = FileInfoHelper.IsOrphaned(exe),
                    TaskPath = task.Path,
                });
            }
            catch { /* 个别任务无法读取时跳过 */ }
        }
        }
        catch { }

        try
        {
            foreach (var sub in folder.SubFolders)
                WalkFolder(sub, output);
        }
        catch { }
    }

    // ================= 自动启动的服务 =================

    private static IEnumerable<StartupEntry> ScanServices()
    {
        var output = new List<StartupEntry>();
        foreach (var sc in ServiceController.GetServices())
        {
            try
            {
                if (sc.StartType != ServiceStartMode.Automatic) continue;

                var name = sc.ServiceName;
                var display = string.IsNullOrWhiteSpace(sc.DisplayName) ? name : sc.DisplayName;
                var imagePath = ReadServiceImagePath(name);
                var (exe, args) = CommandLine.Parse(imagePath);
                var normalized = NormalizeServicePath(exe);
                var delayed = IsDelayedAutoStart(name);
                var description = FileInfoHelper.Description(normalized);

                output.Add(new StartupEntry
                {
                    Id = $"svc|{name}",
                    Name = display,
                    Source = StartupSource.Service,
                    Status = StartupStatus.Enabled,
                    CommandLine = imagePath,
                    ExecutablePath = normalized,
                    Arguments = args,
                    Publisher = FileInfoHelper.Publisher(normalized),
                    Description = string.IsNullOrEmpty(description) ? display : description,
                    Location = $"HKLM\\SYSTEM\\CurrentControlSet\\Services\\{name}{(delayed ? "  (延迟启动)" : "")}",
                    RequiresElevation = true,
                    IsOrphaned = FileInfoHelper.IsOrphaned(normalized),
                    ServiceName = name,
                });
            }
            catch { /* 个别服务无法读取时跳过 */ }
            finally { sc.Dispose(); }
        }
        return output;
    }

    private static string ReadServiceImagePath(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
        return key?.GetValue("ImagePath")?.ToString() ?? "";
    }

    private static bool IsDelayedAutoStart(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
        var value = key?.GetValue("DelayedAutostart");
        return value is int i && i != 0;
    }

    private static string NormalizeServicePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        path = path.Trim('"');
        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            path = Path.Combine(windows, path.Substring(@"\SystemRoot\".Length));
        }
        else if (path.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            path = path.Substring(4);
        }
        try { path = Environment.ExpandEnvironmentVariables(path); } catch { }
        return path;
    }
}

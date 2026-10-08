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
    public ScanResult Scan()
    {
        var items = new List<StartupEntry>();
        var warnings = new List<string>();
        TryAdd(items, warnings, "注册表启动项", ScanRegistryRun);
        TryAdd(items, warnings, "启动文件夹", ScanStartupFolders);
        TryAdd(items, warnings, "计划任务", ScanScheduledTasks);
        TryAdd(items, warnings, "系统服务", ScanServices);

        var entries = items
            .OrderBy(i => i.Source)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new ScanResult { Entries = entries, Warnings = warnings };
    }

    /// <summary>单个来源整体失败时记一条警告,避免与"该来源本来就没有条目"混为一谈。</summary>
    private static void TryAdd(List<StartupEntry> sink, List<string> warnings, string label, Func<IEnumerable<StartupEntry>> producer)
    {
        try { sink.AddRange(producer()); }
        catch (Exception ex) { warnings.Add($"{label}:读取失败({ex.Message})"); }
    }

    // ================= 注册表 Run / RunOnce =================

    private static readonly (RegistryHive Hive, RegistryView View, string SubKey, StartupSource Source)[] RunKeys =
    {
        (RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Run", StartupSource.RegistryRunCurrentUser),
        (RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", StartupSource.RegistryRunOnceCurrentUser),
        (RegistryHive.CurrentUser, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Run", StartupSource.RegistryRunCurrentUser),
        (RegistryHive.CurrentUser, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", StartupSource.RegistryRunOnceCurrentUser),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Run", StartupSource.RegistryRunLocalMachine),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", StartupSource.RegistryRunOnceLocalMachine),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Run", StartupSource.RegistryRunLocalMachine),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", StartupSource.RegistryRunOnceLocalMachine),
    };

    private static IEnumerable<StartupEntry> ScanRegistryRun()
    {
        var output = new List<StartupEntry>();
        foreach (var loc in RunKeys)
        {
            // HKCU\Software 不做 WOW64 重定向:32 位视图和 64 位视图指向同一个物理键,
            // 两遍枚举会得到完全相同的条目。内容一致时只保留 64 位视图那一遍。
            if (loc.View == RegistryView.Registry32 && ViewsAreIdentical(loc.Hive, loc.SubKey))
                continue;

            using var baseKey = RegistryKey.OpenBaseKey(loc.Hive, loc.View);
            using var key = baseKey.OpenSubKey(loc.SubKey, writable: false);
            if (key is null) continue;

            foreach (var rawName in key.GetValueNames())
            {
                if (string.IsNullOrEmpty(rawName)) continue;
                var raw = key.GetValue(rawName)?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(raw)) continue;

                // Run 的"已禁用"记录在 Explorer\StartupApproved 下,与任务管理器一致。
                // RunOnce 的值名可以带 "!"(命令执行完再删除该值)或 "*"(安全模式下也运行)前缀,
                // 这些是 Windows 的合法标志而不是"已停用"标记,因此 RunOnce 保留原名、也不查该标记。
                var isRunOnce = loc.Source is StartupSource.RegistryRunOnceCurrentUser or StartupSource.RegistryRunOnceLocalMachine;
                var name = rawName;
                var disabled = !isRunOnce && StartupApproval.IsRunDisabled(loc.Hive, loc.View == RegistryView.Registry32, name);
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

    /// <summary>
    /// 判断同一位置的 32 位与 64 位视图是否指向同一个物理键(HKCU\Software 不重定向时就会这样)。
    /// 两边内容完全一致时没有必要枚举两遍,否则界面里会出现一模一样的重复条目。
    /// </summary>
    private static bool ViewsAreIdentical(RegistryHive hive, string subKey)
    {
        var view64 = Fingerprint(hive, RegistryView.Registry64, subKey);
        return view64.Length > 0 && view64 == Fingerprint(hive, RegistryView.Registry32, subKey);
    }

    private static string Fingerprint(RegistryHive hive, RegistryView view, string subKey)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey, writable: false);
            if (key is null) return "";

            var values = key.GetValueNames()
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => $"{n}={key.GetValue(n)}")
                .ToArray();
            Array.Sort(values, StringComparer.Ordinal);
            return string.Join("\n", values);
        }
        catch
        {
            return "";
        }
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
                InDisabledSubfolder = inDisabledSubfolder,
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

        foreach (var sub in folder.SubFolders)
        {
            try { WalkFolder(sub, output); }
            catch { /* 个别子文件夹不可读时跳过 */ }
        }
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
                    IsDelayedAutoStart = delayed,
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

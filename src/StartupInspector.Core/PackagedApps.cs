
using Microsoft.Win32;

namespace StartupInspector.Core;

/// <summary>
/// 打包应用(Store / MSIX)的"启动任务"。这一类不写在 Run 里,而是注册在
/// HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData
/// \&lt;包族名&gt;\&lt;任务Id&gt; 下,由 State 值控制(对应 Windows.ApplicationModel.StartupTaskState:
/// 0 = 已停用,2 = 已启用)。任务管理器的"启动"页显示的就是这一类,再加上 Run 和启动文件夹。
/// </summary>
internal static class PackagedApps
{
    private const string SystemAppDataPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    // Windows.ApplicationModel.StartupTaskState:0 = Disabled(清单默认),1 = DisabledByUser,2 = Enabled
    private const int StateDisabled = 1;
    private const int StateEnabled = 2;

    /// <summary>扫描当前用户的打包应用启动任务。</summary>
    public static IReadOnlyList<StartupEntry> Scan()
    {
        using var appModel = Registry.CurrentUser.OpenSubKey(SystemAppDataPath, writable: false);
        if (appModel is null) return Array.Empty<StartupEntry>();

        var displayNames = FamilyNames();
        var entries = new List<StartupEntry>();

        foreach (var familyName in appModel.GetSubKeyNames())
        {
            using var package = appModel.OpenSubKey(familyName, writable: false);
            if (package is null) continue;

            foreach (var taskId in package.GetSubKeyNames())
            {
                using var task = package.OpenSubKey(taskId, writable: false);
                // 只有带 State 值的子键才是启动任务,其余是 AppModel 自己的其他数据。
                if (task?.GetValue("State") is not int state) continue;

                entries.Add(new StartupEntry
                {
                    Id = $"appx|{familyName}|{taskId}",
                    Name = displayNames.TryGetValue(familyName, out var display) ? display : CleanFamilyName(familyName),
                    Source = StartupSource.PackagedAppStartupTask,
                    Status = state == StateEnabled ? StartupStatus.Enabled : StartupStatus.Disabled,
                    CommandLine = $"{familyName}!{taskId}",
                    ExecutablePath = familyName,
                    Location = $@"HKCU\{SystemAppDataPath}\{familyName}\{taskId}",
                    RequiresElevation = false,
                    IsOrphaned = false,
                    PackageFamilyName = familyName,
                    PackageTaskId = taskId,
                });
            }
        }

        return entries;
    }

    /// <summary>启用 / 停用打包应用的启动任务。写的是用户自己的 HKCU,不需要管理员。</summary>
    public static ControlResult SetEnabled(StartupEntry entry, bool enable)
    {
        if (entry.PackageFamilyName is null || entry.PackageTaskId is null)
            return ControlResult.Fail("缺少打包应用信息");

        try
        {
            var path = $@"{SystemAppDataPath}\{entry.PackageFamilyName}\{entry.PackageTaskId}";
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
            if (key is null) return ControlResult.Fail("找不到该启动任务");

            key.SetValue("State", enable ? StateEnabled : StateDisabled, RegistryValueKind.DWord);
            return ControlResult.Ok(enable ? "已启用" : "已停用");
        }
        catch (UnauthorizedAccessException)
        {
            return ControlResult.Fail("访问被拒绝");
        }
        catch (Exception ex)
        {
            return ControlResult.Fail(ex.Message);
        }
    }

    /// <summary>拿不到显示名时的兜底:28017CharlesMilette.TranslucentTB_v826wp6bftszj -> TranslucentTB。</summary>
    internal static string CleanFamilyName(string familyName)
    {
        var name = familyName;

        var underscore = name.LastIndexOf('_');
        if (underscore > 0) name = name[..underscore];

        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];

        return name.Length > 0 ? name : familyName;
    }

    /// <summary>从 shell:AppsFolder 取应用显示名(跟随系统语言),失败时返回空表。</summary>
    private static Dictionary<string, string> FamilyNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return map;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic folder = shell.Namespace("shell:AppsFolder");
            if (folder is null) return map;

            dynamic items = folder.Items();
            var count = (int)items.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic item = items.Item(i);
                // 打包应用在 AppsFolder 里的路径形如 <包族名>!<入口Id>
                var appUserModelId = item.Path as string ?? "";
                var bang = appUserModelId.IndexOf('!');
                if (bang <= 0) continue;

                var familyName = appUserModelId[..bang];
                var display = item.Name as string ?? "";
                if (display.Length > 0 && !map.ContainsKey(familyName)) map[familyName] = display;
            }
        }
        catch
        {
            // 枚举失败就用 CleanFamilyName 兜底
        }

        return map;
    }
}

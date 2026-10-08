using Microsoft.Win32;

namespace StartupInspector.Core;

/// <summary>
/// Windows / 任务管理器记录自启项"已启用 / 已禁用"的机制:在
/// Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{Run|Run32|StartupFolder}
/// 下为每一项存一个 12 字节值,首字节 0x02 = 启用、0x03 = 禁用(禁用时后 8 字节是时间戳)。
/// 这些键都位于注册表的 64 位(原生)视图。
/// </summary>
internal static class StartupApproval
{
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const byte StateEnabled = 0x02;
    private const byte StateDisabled = 0x03;

    public static bool IsRunDisabled(RegistryHive hive, bool is32Bit, string valueName)
        => FirstByte(hive, RunSubKey(is32Bit), valueName) == StateDisabled;

    public static bool IsFolderDisabled(RegistryHive hive, string fileName)
    {
        // Windows 会同时写 "xxx.lnk" 和 "xxx" 两个值名,第三方工具可能只写其中一个,这里两个都查。
        if (FirstByte(hive, FolderSubKey, fileName) == StateDisabled) return true;

        var withoutExtension = Path.GetFileNameWithoutExtension(fileName);
        return !string.Equals(withoutExtension, fileName, StringComparison.OrdinalIgnoreCase)
            && FirstByte(hive, FolderSubKey, withoutExtension) == StateDisabled;
    }

    public static void SetRunDisabled(RegistryHive hive, bool is32Bit, string valueName, bool disabled)
        => Write(hive, RunSubKey(is32Bit), valueName, disabled);

    public static void SetFolderDisabled(RegistryHive hive, string fileName, bool disabled)
    {
        Write(hive, FolderSubKey, fileName, disabled);
        var withoutExtension = Path.GetFileNameWithoutExtension(fileName);
        if (!string.Equals(withoutExtension, fileName, StringComparison.OrdinalIgnoreCase))
            Write(hive, FolderSubKey, withoutExtension, disabled);
    }

    public static void RemoveRun(RegistryHive hive, bool is32Bit, string valueName)
        => Delete(hive, RunSubKey(is32Bit), valueName);

    public static void RemoveFolder(RegistryHive hive, string fileName)
    {
        Delete(hive, FolderSubKey, fileName);
        Delete(hive, FolderSubKey, Path.GetFileNameWithoutExtension(fileName));
    }

    private static string RunSubKey(bool is32Bit) => Root + (is32Bit ? @"\Run32" : @"\Run");

    private static string FolderSubKey => Root + @"\StartupFolder";

    private static byte? FirstByte(RegistryHive hive, string subKey, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey, writable: false);
            if (key?.GetValue(name) is byte[] bytes && bytes.Length > 0)
                return bytes[0];
        }
        catch { }
        return null;
    }

    private static void Write(RegistryHive hive, string subKey, string name, bool disabled)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(subKey, writable: true);

        var value = new byte[12];
        value[0] = disabled ? StateDisabled : StateEnabled;
        if (disabled)
        {
            var stamp = BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc());
            Array.Copy(stamp, 0, value, 4, stamp.Length);
        }
        key.SetValue(name, value, RegistryValueKind.Binary);
    }

    private static void Delete(RegistryHive hive, string subKey, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey, writable: true);
            key?.DeleteValue(name, false);
        }
        catch { }
    }
}

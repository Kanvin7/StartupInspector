using System.Diagnostics;

namespace StartupInspector.Core;

/// <summary>把命令行拆成"可执行文件 + 参数"。</summary>
internal static class CommandLine
{
    public static (string Exe, string Args) Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ("", "");
        raw = raw.Trim();

        if (raw.StartsWith('"'))
        {
            var end = raw.IndexOf('"', 1);
            if (end > 0)
                return (raw.Substring(1, end - 1), raw[(end + 1)..].TrimStart());
            return (raw.Trim('"'), "");
        }

        // 未加引号:优先在第一个 .exe 处切分(能处理带空格的路径)。
        var exeIndex = raw.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
        {
            var cut = exeIndex + 4;
            var candidate = raw[..cut];
            if (File.Exists(candidate))
                return (candidate, raw[cut..].TrimStart());
        }

        var space = raw.IndexOf(' ');
        if (space < 0) return (raw, "");
        var first = raw[..space];
        if (File.Exists(first)) return (first, raw[(space + 1)..]);
        return (raw, "");
    }
}

/// <summary>读取可执行文件的发布者/描述信息,并判断是否已失效。</summary>
internal static class FileInfoHelper
{
    public static string Publisher(string path) => Version(path)?.CompanyName?.Trim() ?? "";

    public static string Description(string path) => Version(path)?.FileDescription?.Trim() ?? "";

    public static bool IsOrphaned(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return !File.Exists(path); }
        catch { return false; }
    }

    private static FileVersionInfo? Version(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try { return FileVersionInfo.GetVersionInfo(path); }
        catch { return null; }
    }
}

/// <summary>解析 .lnk 快捷方式的目标(通过 WScript.Shell 晚绑定,免额外依赖)。</summary>
internal static class ShortcutResolver
{
    public static (string ExecutablePath, string Arguments) Resolve(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return (path, "");

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return (path, "");

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(path);
            string target = shortcut.TargetPath as string ?? "";
            string args = shortcut.Arguments as string ?? "";
            return (target, args);
        }
        catch
        {
            return (path, "");
        }
    }
}

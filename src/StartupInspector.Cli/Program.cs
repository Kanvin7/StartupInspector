using System.Text;
using StartupInspector.Core;

Console.OutputEncoding = Encoding.UTF8;

var (mode, outPath) = ParseArgs(args);

var result = new StartupScanner().Scan();
var entries = result.Entries;

// 来源整体读取失败时写到标准错误,导出到文件时也能在控制台看到。
foreach (var warning in result.Warnings)
    Console.Error.WriteLine($"警告:{warning}");

switch (mode)
{
    case "csv":
        if (outPath is not null)
        {
            StartupExporter.SaveCsv(outPath, entries);
            Console.WriteLine($"已导出 {entries.Count} 条到 {outPath}");
        }
        else
        {
            Console.Write(StartupExporter.ToCsv(entries));
        }
        break;

    case "json":
        if (outPath is not null)
        {
            StartupExporter.SaveJson(outPath, entries);
            Console.WriteLine($"已导出 {entries.Count} 条到 {outPath}");
        }
        else
        {
            Console.Write(StartupExporter.ToJson(entries));
        }
        break;

    default:
        PrintTable(entries);
        break;
}

// 第一个非选项参数作为子命令(scan/csv/json);支持 --out/-o 指定输出文件。
// 逐项解析而不是"取第一个不以 -- 开头的参数",避免把 --out 的值当成子命令。
static (string Mode, string? OutPath) ParseArgs(string[] arguments)
{
    var mode = "scan";
    string? outPath = null;

    for (var i = 0; i < arguments.Length; i++)
    {
        var arg = arguments[i];
        if (string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "-o", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 < arguments.Length) outPath = arguments[++i];
            continue;
        }

        if (arg.StartsWith("-", StringComparison.Ordinal)) continue;
        mode = arg.ToLowerInvariant();
    }

    return (mode, outPath);
}

static void PrintTable(IReadOnlyList<StartupEntry> entries)
{
    Console.WriteLine($"共发现 {entries.Count} 条开机自启项");
    Console.WriteLine($"当前权限:{(Elevation.IsElevated() ? "管理员" : "普通用户(修改 HKLM / 服务 / 系统计划任务需要管理员)")}");
    Console.WriteLine();

    var source = entries.GroupBy(e => e.Source).OrderBy(g => g.Key);
    foreach (var group in source)
        Console.WriteLine($"  {StartupLabels.SourceText(group.Key)}: {group.Count()} 条");
    Console.WriteLine();

    foreach (var e in entries)
    {
        Console.WriteLine($"[{StartupLabels.StatusText(e.Status)}] {e.Name}");
        Console.WriteLine($"    来源: {StartupLabels.SourceText(e.Source)}");
        Console.WriteLine($"    路径: {e.ExecutablePath}");
        if (e.Publisher.Length > 0) Console.WriteLine($"    发布者: {e.Publisher}");
        Console.WriteLine($"    位置: {e.Location}");
        Console.WriteLine();
    }
}

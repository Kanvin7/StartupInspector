using System.Text;
using StartupInspector.Core;

Console.OutputEncoding = Encoding.UTF8;

var entries = new StartupScanner().Scan();

var mode = args.FirstOrDefault(a => !a.StartsWith("--"))?.ToLowerInvariant() ?? "scan";
var outPath = GetOption(args, "--out");

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

static string? GetOption(string[] arguments, string name)
{
    for (var i = 0; i < arguments.Length - 1; i++)
    {
        if (string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase))
            return arguments[i + 1];
    }
    return null;
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


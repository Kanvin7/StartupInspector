
using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;

namespace StartupInspector.Core;

/// <summary>
/// 最近一次开机时每个自启组件花了多少时间,来自"诊断-性能"日志
/// (Microsoft-Windows-Diagnostics-Performance/Operational 的事件 101 应用 / 103 服务,
/// 事件里的 TotalTime 单位是毫秒)。任务管理器的"启动影响"用的就是这一类数据。
///
/// 这条日志默认只有管理员能读,读不到时全部按"未知"处理,不会影响其他功能。
/// </summary>
public static class StartupImpact
{
    private const string LogName = "Microsoft-Windows-Diagnostics-Performance/Operational";

    /// <summary>最多读多少条匹配记录:从旧到新读,够最近几次开机用即可。</summary>
    private const int MaxRecords = 600;

    /// <summary>同一次开机的记录时间戳几乎相同,超过这个跨度就不算同一次。</summary>
    private static readonly TimeSpan SameBootWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 取最近一次开机中每个组件的耗时。键同时包含完整路径和唯一的文件名。
    /// 无法读取日志(通常是权限不足)时返回 null。
    /// </summary>
    public static IReadOnlyDictionary<string, int>? LatestBootTimes()
    {
        try
        {
            var query = new EventLogQuery(LogName, PathType.LogName, "*[System[(EventID=101 or EventID=103)]]");
            using var reader = new EventLogReader(query);

            var collected = new List<(DateTime When, string Path, int Total)>();
            var newest = DateTime.MinValue;

            while (collected.Count < MaxRecords)
            {
                using var record = reader.ReadEvent();
                if (record is null) break;
                if (!TryReadTime(record, out var path, out var total)) continue;

                var when = record.TimeCreated ?? DateTime.MinValue;
                if (when > newest) newest = when;
                collected.Add((when, path, total));
            }

            if (newest == DateTime.MinValue) return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var latest = collected.Where(c => newest - c.When <= SameBootWindow).ToList();
            var times = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in latest)
            {
                // 同一组件可能有多条记录,取较大的那个耗时
                if (!times.TryGetValue(item.Path, out var existing) || item.Total > existing)
                    times[item.Path] = item.Total;

                var file = Path.GetFileName(item.Path);
                if (file.Length == 0) continue;
                nameCounts[file] = nameCounts.TryGetValue(file, out var count) ? count + 1 : 1;
            }

            // 文件名兜底:只有在本次开机记录里唯一时才有意义(svchost.exe 这种会撞名)
            foreach (var item in latest)
            {
                var file = Path.GetFileName(item.Path);
                if (file.Length > 0 && nameCounts[file] == 1 && !times.ContainsKey(file))
                    times[file] = times[item.Path];
            }

            return times;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把耗时填到条目上。</summary>
    public static void Assign(IReadOnlyList<StartupEntry> entries, IReadOnlyDictionary<string, int>? times)
    {
        if (times is null || times.Count == 0) return;

        // 同名可执行文件没法区分,只在条目里名字唯一时才用文件名兜底
        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var file = Path.GetFileName(entry.ExecutablePath);
            if (file.Length == 0) continue;
            nameCounts[file] = nameCounts.TryGetValue(file, out var count) ? count + 1 : 1;
        }

        foreach (var entry in entries)
        {
            if (entry.ExecutablePath.Length > 0 && times.TryGetValue(entry.ExecutablePath, out var ms))
            {
                entry.StartupMilliseconds = ms;
                continue;
            }

            var file = Path.GetFileName(entry.ExecutablePath);
            if (file.Length > 0 && nameCounts[file] == 1 && times.TryGetValue(file, out var byName))
                entry.StartupMilliseconds = byName;
        }
    }

    /// <summary>界面上的耗时文本,未知返回 null。</summary>
    public static string? Describe(int? milliseconds) => milliseconds switch
    {
        null or < 0 => null,
        < 1000 => $"{milliseconds} 毫秒",
        _ => $"{milliseconds.Value / 1000.0:0.0} 秒",
    };

    /// <summary>粗分的影响等级:0.8 秒以内算低,2 秒以上算高;未知返回 null。</summary>
    public static string? Level(int? milliseconds) => milliseconds switch
    {
        null or < 0 => null,
        < 800 => "低",
        < 2000 => "中",
        _ => "高",
    };

    private static bool TryReadTime(EventRecord record, out string path, out int totalMilliseconds)
    {
        path = "";
        totalMilliseconds = 0;
        try
        {
            var document = XDocument.Parse(record.ToXml());
            var data = document.Descendants().Where(e => e.Name.LocalName == "Data").ToList();

            string? Value(string name)
                => data.FirstOrDefault(d => (string?)d.Attribute("Name") == name)?.Value;

            if (!int.TryParse(Value("TotalTime"), out totalMilliseconds)) return false;

            path = Value("Path") ?? Value("Name") ?? "";
            return path.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}


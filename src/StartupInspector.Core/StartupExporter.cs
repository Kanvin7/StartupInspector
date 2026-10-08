using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StartupInspector.Core;

/// <summary>把扫描结果导出为 CSV / JSON。</summary>
public static class StartupExporter
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static string ToJson(IEnumerable<StartupEntry> entries)
        => JsonSerializer.Serialize(entries, JsonOptions);

    public static string ToCsv(IEnumerable<StartupEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("名称,来源,状态,开机耗时(毫秒),发布者,可执行文件,参数,位置,需管理员,已失效");
        foreach (var e in entries)
        {
            sb.AppendLine(string.Join(",",
                Field(e.Name),
                Field(StartupLabels.SourceText(e.Source)),
                Field(StartupLabels.StatusText(e.Status)),
                e.StartupMilliseconds?.ToString() ?? "",
                Field(e.Publisher),
                Field(e.ExecutablePath),
                Field(e.Arguments),
                Field(e.Location),
                e.RequiresElevation ? "是" : "否",
                e.IsOrphaned ? "是" : "否"));
        }
        return sb.ToString();
    }

    public static void SaveCsv(string path, IEnumerable<StartupEntry> entries)
        => File.WriteAllText(path, ToCsv(entries), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    public static void SaveJson(string path, IEnumerable<StartupEntry> entries)
        => File.WriteAllText(path, ToJson(entries), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static string Field(string? value)
    {
        value ??= "";
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}

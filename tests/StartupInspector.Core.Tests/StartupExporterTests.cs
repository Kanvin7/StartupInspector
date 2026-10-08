using Xunit;

namespace StartupInspector.Core.Tests;

public class StartupExporterTests
{
    [Fact]
    public void Csv_starts_with_the_column_header()
    {
        var csv = StartupExporter.ToCsv(Array.Empty<StartupEntry>());

        Assert.StartsWith("名称,来源,状态,发布者,可执行文件,参数,位置,需管理员,已失效", csv);
    }

    [Fact]
    public void Csv_leaves_plain_fields_unquoted()
    {
        var csv = StartupExporter.ToCsv(new[] { new StartupEntry { Name = "plain" } });

        Assert.Contains("plain,注册表 Run (当前用户)", csv);
    }

    [Fact]
    public void Csv_quotes_fields_containing_separators_or_quotes()
    {
        var entry = new StartupEntry
        {
            Name = "a,b",
            Publisher = "say \"hi\"",
            Location = "line1\nline2",
        };

        var csv = StartupExporter.ToCsv(new[] { entry });

        Assert.Contains("\"a,b\"", csv);
        Assert.Contains("\"say \"\"hi\"\"\"", csv);
        Assert.Contains("\"line1\nline2\"", csv);
    }

    [Fact]
    public void Json_uses_enum_names_and_hides_internal_members()
    {
        var json = StartupExporter.ToJson(new[] { new StartupEntry { Name = "x", Source = StartupSource.Service } });

        Assert.Contains("\"Source\": \"Service\"", json);
        Assert.DoesNotContain("RegistryPath", json);
        Assert.DoesNotContain("RegistryValueName", json);
        Assert.DoesNotContain("ServiceName", json);
        Assert.DoesNotContain("IsRunOnce", json);
        Assert.DoesNotContain("CanToggle", json);
    }
}


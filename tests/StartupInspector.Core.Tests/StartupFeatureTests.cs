
using System.Text.Json;
using Microsoft.Win32;
using Xunit;

namespace StartupInspector.Core.Tests;

public class StartupFeatureTests
{
    [Theory]
    [InlineData("28017CharlesMilette.TranslucentTB_v826wp6bftszj", "TranslucentTB")]
    [InlineData("Microsoft.PowerAutomateDesktop_8wekyb3d8bbwe", "PowerAutomateDesktop")]
    [InlineData("MicrosoftWindows.CrossDevice_cw5n1h2txyewy", "CrossDevice")]
    [InlineData("aimgr_8wekyb3d8bbwe", "aimgr")]
    public void Packaged_app_family_names_fall_back_to_something_readable(string familyName, string expected)
        => Assert.Equal(expected, PackagedApps.CleanFamilyName(familyName));

    [Theory]
    [InlineData(635, "635 毫秒")]
    [InlineData(1341, "1.3 秒")]
    [InlineData(null, null)]
    public void Impact_text_is_formatted_for_display(int? milliseconds, string? expected)
        => Assert.Equal(expected, StartupImpact.Describe(milliseconds));

    [Theory]
    [InlineData(100, "低")]
    [InlineData(799, "低")]
    [InlineData(800, "中")]
    [InlineData(1999, "中")]
    [InlineData(2000, "高")]
    [InlineData(null, null)]
    public void Impact_levels_use_fixed_thresholds(int? milliseconds, string? expected)
        => Assert.Equal(expected, StartupImpact.Level(milliseconds));

    [Fact]
    public void Impact_matches_by_full_path_and_only_by_unique_file_name()
    {
        var first = new StartupEntry { ExecutablePath = @"C:\A\app.exe" };
        var second = new StartupEntry { ExecutablePath = @"C:\B\app.exe" };
        var solo = new StartupEntry { ExecutablePath = @"C:\C\solo.exe" };
        var times = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\A\app.exe"] = 1200,
            ["solo.exe"] = 300,
        };

        StartupImpact.Assign(new[] { first, second, solo }, times);

        Assert.Equal(1200, first.StartupMilliseconds);
        Assert.Null(second.StartupMilliseconds);
        Assert.Equal(300, solo.StartupMilliseconds);
    }

    [Fact]
    public void Packaged_app_scan_never_throws()
    {
        var entries = PackagedApps.Scan();

        Assert.NotNull(entries);
        Assert.All(entries, e =>
        {
            Assert.Equal(StartupSource.PackagedAppStartupTask, e.Source);
            Assert.False(string.IsNullOrWhiteSpace(e.Name));
            Assert.True(e.CanToggle);
        });
    }

    [Fact]
    public void Backup_file_survives_a_json_round_trip()
    {
        var original = new BackupFile
        {
            CreatedAt = "2026-10-09 10:00:00",
            Machine = "TEST",
            Items =
            {
                new BackupItem
                {
                    Kind = "registryRun",
                    DisplayName = "demo",
                    Source = "RegistryRunCurrentUser",
                    WasDisabled = true,
                    Hive = "CurrentUser",
                    View = "Registry64",
                    SubKey = @"Software\Microsoft\Windows\CurrentVersion\Run",
                    ValueName = "demo",
                    ValueKind = "String",
                    Value = @"C:\demo.exe --flag",
                },
            },
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<BackupFile>(json)!;

        var item = Assert.Single(restored.Items);
        Assert.Equal(original.CreatedAt, restored.CreatedAt);
        Assert.Equal("registryRun", item.Kind);
        Assert.True(item.WasDisabled);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", item.SubKey);
        Assert.Equal(@"C:\demo.exe --flag", item.Value);
    }

    [Fact]
    public void Deleting_a_registry_value_can_be_undone_from_the_backup()
    {
        // 用一个临时项做真实的删除 + 还原往返,不碰系统真正的自启项
        const string subKey = @"Software\StartupInspectorTests\RunRoundTrip";
        var valueName = "StartupInspectorRoundTripTest";
        const string command = @"C:\temp\demo.exe --flag";

        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true))
                key!.SetValue(valueName, command, RegistryValueKind.String);

            var entry = new StartupEntry
            {
                Name = valueName,
                Source = StartupSource.RegistryRunCurrentUser,
                Status = StartupStatus.Enabled,
                Hive = RegistryHive.CurrentUser,
                View = RegistryView.Registry64,
                RegistryPath = subKey,
                RegistryValueName = valueName,
            };

            var captured = BackupStore.Capture(entry);
            Assert.NotNull(captured);
            Assert.Equal("registryRun", captured!.Kind);
            Assert.Equal("String", captured.ValueKind);
            Assert.Equal(command, captured.Value);

            using (var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true))
                key!.DeleteValue(valueName, false);

            var outcomes = BackupStore.Restore(new BackupFile { Items = new List<BackupItem> { captured } });

            Assert.True(Assert.Single(outcomes).Success);
            using (var key = Registry.CurrentUser.OpenSubKey(subKey))
                Assert.Equal(command, key!.GetValue(valueName));
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false); } catch { }
            try { StartupApproval.RemoveRun(RegistryHive.CurrentUser, is32Bit: false, valueName); } catch { }
        }
    }
}


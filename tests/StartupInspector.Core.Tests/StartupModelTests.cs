using Xunit;

namespace StartupInspector.Core.Tests;

public class StartupModelTests
{
    [Theory]
    [InlineData(StartupSource.RegistryRunCurrentUser, "注册表 Run (当前用户)")]
    [InlineData(StartupSource.RegistryRunOnceCurrentUser, "注册表 RunOnce (当前用户)")]
    [InlineData(StartupSource.RegistryRunLocalMachine, "注册表 Run (本机)")]
    [InlineData(StartupSource.RegistryRunOnceLocalMachine, "注册表 RunOnce (本机)")]
    [InlineData(StartupSource.StartupFolderCurrentUser, "启动文件夹 (当前用户)")]
    [InlineData(StartupSource.StartupFolderAllUsers, "启动文件夹 (所有用户)")]
    [InlineData(StartupSource.ScheduledTask, "计划任务")]
    [InlineData(StartupSource.Service, "系统服务")]
    public void Every_source_has_a_distinct_label(StartupSource source, string expected)
        => Assert.Equal(expected, StartupLabels.SourceText(source));

    [Fact]
    public void RunOnce_entries_cannot_be_toggled()
    {
        Assert.False(new StartupEntry { Source = StartupSource.RegistryRunOnceCurrentUser }.CanToggle);
        Assert.False(new StartupEntry { Source = StartupSource.RegistryRunOnceLocalMachine }.CanToggle);
        Assert.True(new StartupEntry { Source = StartupSource.RegistryRunCurrentUser }.CanToggle);
        Assert.True(new StartupEntry { Source = StartupSource.Service }.CanToggle);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Toggling_a_RunOnce_entry_is_reported_as_unsupported(bool enable)
    {
        var controller = new StartupController();
        var entry = new StartupEntry { Source = StartupSource.RegistryRunOnceCurrentUser };

        var result = controller.SetEnabled(entry, enable);

        Assert.False(result.Success);
        Assert.Contains("RunOnce", result.Message ?? "");
    }

    [Fact]
    public void Scan_returns_entries_with_unique_ids_and_a_warning_list()
    {
        var result = new StartupScanner().Scan();

        Assert.NotNull(result.Entries);
        Assert.NotNull(result.Warnings);
        Assert.All(result.Entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Id)));
        Assert.Equal(result.Entries.Count, result.Entries.Select(e => e.Id).Distinct().Count());
    }
}


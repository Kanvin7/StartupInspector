using Xunit;

namespace StartupInspector.Core.Tests;

public class CommandLineTests
{
    [Fact]
    public void Quoted_path_and_arguments_are_split()
    {
        var (exe, args) = CommandLine.Parse("\"C:\\Program Files\\Foo\\foo.exe\" --bar baz");

        Assert.Equal("C:\\Program Files\\Foo\\foo.exe", exe);
        Assert.Equal("--bar baz", args);
    }

    [Fact]
    public void Quoted_path_without_arguments_yields_empty_arguments()
    {
        var (exe, args) = CommandLine.Parse("\"C:\\Program Files\\Foo\\foo.exe\"");

        Assert.Equal("C:\\Program Files\\Foo\\foo.exe", exe);
        Assert.Equal("", args);
    }

    [Fact]
    public void Unterminated_quote_still_yields_the_path()
    {
        var (exe, args) = CommandLine.Parse("\"C:\\Foo\\foo.exe");

        Assert.Equal("C:\\Foo\\foo.exe", exe);
        Assert.Equal("", args);
    }

    [Fact]
    public void Path_without_spaces_is_returned_as_is()
    {
        var (exe, args) = CommandLine.Parse("C:\\Definitely\\Missing\\foo.exe");

        Assert.Equal("C:\\Definitely\\Missing\\foo.exe", exe);
        Assert.Equal("", args);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_yields_an_empty_pair(string? raw)
    {
        var (exe, args) = CommandLine.Parse(raw);

        Assert.Equal("", exe);
        Assert.Equal("", args);
    }
}


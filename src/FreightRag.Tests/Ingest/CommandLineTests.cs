using FreightRag.Ingest;

namespace FreightRag.Tests.Ingest;

public class CommandLineTests
{
    public static TheoryData<string[], string> ValidArgs() => new()
    {
        { ["--data-dir", "data"], "data" },
        { ["--data-dir=/data"], "/data" },
    };

    [Theory]
    [MemberData(nameof(ValidArgs))]
    public void Parses_the_data_dir_argument(string[] args, string expected)
    {
        Assert.Equal(expected, CommandLine.ParseDataDir(args));
    }

    public static TheoryData<string[]> InvalidArgs() => new()
    {
        Array.Empty<string>(),
        new[] { "--data-dir" },
        new[] { "--other", "x" },
        new[] { "--data-dir", "" },
    };

    [Theory]
    [MemberData(nameof(InvalidArgs))]
    public void Missing_data_dir_argument_is_an_error(string[] args)
    {
        var ex = Assert.Throws<ArgumentException>(() => CommandLine.ParseDataDir(args));
        Assert.Contains("--data-dir", ex.Message);
    }
}

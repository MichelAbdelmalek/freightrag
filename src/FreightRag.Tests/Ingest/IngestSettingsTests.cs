using FreightRag.Ingest;
using Microsoft.Extensions.Configuration;

namespace FreightRag.Tests.Ingest;

public class IngestSettingsTests
{
    private const string Secret = "not-a-real-password-123";

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> ValidAppSettings() => new()
    {
        ["Database:Host"] = "host.docker.internal",
        ["Database:Port"] = "55432",
        ["Database:Name"] = "freightrag",
        ["Database:Username"] = "freightrag",
        ["Ollama:Url"] = "http://host.docker.internal:11434",
        ["Ollama:EmbeddingModel"] = "nomic-embed-text",
        ["Tenants:tenant-nile-freight"] = "de1c81e9-4aa5-4f07-87bd-c5d6f48ff097",
        ["Tenants:tenant-delta-haul"] = "14a2de5d-56c5-4eda-a737-d957f4580b90",
    };

    private static IConfiguration Secrets(string? password = Secret) =>
        Config(new() { ["Database:Password"] = password });

    [Fact]
    public void Loads_settings_and_takes_the_password_from_user_secrets()
    {
        var settings = IngestSettings.Load(Config(ValidAppSettings()), Secrets());

        Assert.Equal("host.docker.internal", settings.Database.Host);
        Assert.Equal(55432, settings.Database.Port);
        Assert.Equal(new Uri("http://host.docker.internal:11434"), settings.Ollama.Url);
        Assert.Equal(2, settings.Tenants.Count);
        Assert.Contains($"Password={Secret}", settings.Database.ConnectionString);
    }

    [Fact]
    public void The_safe_description_never_contains_the_password()
    {
        var settings = IngestSettings.Load(Config(ValidAppSettings()), Secrets());

        Assert.Equal("host.docker.internal:55432/freightrag as freightrag", settings.Database.Describe());
        Assert.DoesNotContain(Secret, settings.Database.Describe());
        Assert.DoesNotContain(Secret, settings.Database.ToString()); // e.g. if someone logs the object
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_password_gives_a_clear_error_that_explains_user_secrets(string? password)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => IngestSettings.Load(Config(ValidAppSettings()), Secrets(password)));
        Assert.Contains("user-secrets", ex.Message);
    }

    [Fact]
    public void A_password_in_appsettings_is_refused()
    {
        var appSettings = ValidAppSettings();
        appSettings["Database:Password"] = "oops";

        var ex = Assert.Throws<InvalidOperationException>(() => IngestSettings.Load(Config(appSettings), Secrets()));
        Assert.Contains("appsettings.json", ex.Message);
        Assert.DoesNotContain("oops", ex.Message);
    }

    [Theory]
    [InlineData("Database:Host")]
    [InlineData("Database:Name")]
    [InlineData("Database:Username")]
    [InlineData("Ollama:Url")]
    [InlineData("Ollama:EmbeddingModel")]
    public void A_missing_required_setting_is_named_in_the_error(string key)
    {
        var appSettings = ValidAppSettings();
        appSettings.Remove(key);

        var ex = Assert.Throws<InvalidOperationException>(() => IngestSettings.Load(Config(appSettings), Secrets()));
        Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void No_tenant_mappings_is_an_error()
    {
        var appSettings = ValidAppSettings();
        appSettings.Remove("Tenants:tenant-nile-freight");
        appSettings.Remove("Tenants:tenant-delta-haul");

        var ex = Assert.Throws<InvalidOperationException>(() => IngestSettings.Load(Config(appSettings), Secrets()));
        Assert.Contains("Tenants", ex.Message);
    }

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

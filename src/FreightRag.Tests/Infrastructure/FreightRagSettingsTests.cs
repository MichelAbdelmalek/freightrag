using FreightRag.Core.Infrastructure.Configuration;
using FreightRag.Tests.Support;
using Microsoft.Extensions.Configuration;

namespace FreightRag.Tests.Infrastructure;

public class FreightRagSettingsTests
{
    // Fake credentials are generated at runtime, so the source contains no password literals
    // (secret scanners rightly flag those, even in tests).
    private static readonly string Secret = TestSecrets.NewFakeSecret();

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

    private static IConfiguration Secrets() => Secrets(Secret);

    private static IConfiguration Secrets(string? password) =>
        Config(new() { ["Database:Password"] = password });

    [Fact]
    public void Loads_settings_and_takes_the_password_from_user_secrets()
    {
        var settings = FreightRagSettings.Load(Config(ValidAppSettings()), Secrets());

        Assert.Equal("host.docker.internal", settings.Database.Host);
        Assert.Equal(55432, settings.Database.Port);
        Assert.Equal(new Uri("http://host.docker.internal:11434"), settings.Ollama.Url);
        Assert.Equal(2, settings.Tenants.Count);
        // Parse rather than substring-match: the builder quotes values that contain '=' or ';'.
        Assert.Equal(Secret, new Npgsql.NpgsqlConnectionStringBuilder(settings.Database.ConnectionString).Password);
    }

    [Fact]
    public void The_safe_description_never_contains_the_password()
    {
        var settings = FreightRagSettings.Load(Config(ValidAppSettings()), Secrets());

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
        var ex = Assert.Throws<InvalidOperationException>(() => FreightRagSettings.Load(Config(ValidAppSettings()), Secrets(password)));
        Assert.Contains("user-secrets", ex.Message);
    }

    [Fact]
    public void A_password_in_appsettings_is_refused()
    {
        var appSettings = ValidAppSettings();
        var misplaced = TestSecrets.NewFakeSecret();
        appSettings["Database:Password"] = misplaced;

        var ex = Assert.Throws<InvalidOperationException>(() => FreightRagSettings.Load(Config(appSettings), Secrets()));
        Assert.Contains("appsettings.json", ex.Message);
        Assert.DoesNotContain(misplaced, ex.Message);
    }

    [Fact]
    public void A_password_from_environment_variables_is_refused()
    {
        // Hosts like ASP.NET Core add environment variables to the non-secret configuration;
        // Database__Password=... would surface as Database:Password there.
        var fromEnvironment = TestSecrets.NewFakeSecret();
        var nonSecret = new ConfigurationBuilder()
            .AddInMemoryCollection(ValidAppSettings())
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Password"] = fromEnvironment })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => FreightRagSettings.Load(nonSecret, Secrets()));
        Assert.Contains("environment variables", ex.Message);
        Assert.DoesNotContain(fromEnvironment, ex.Message);
    }

    [Fact]
    public void The_application_name_is_shown_to_postgres()
    {
        var settings = FreightRagSettings.Load(Config(ValidAppSettings()), Secrets(), applicationName: "FreightRag.Api");

        Assert.Contains("Application Name=FreightRag.Api", settings.Database.ConnectionString);
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

        var ex = Assert.Throws<InvalidOperationException>(() => FreightRagSettings.Load(Config(appSettings), Secrets()));
        Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void No_tenant_mappings_is_an_error()
    {
        var appSettings = ValidAppSettings();
        appSettings.Remove("Tenants:tenant-nile-freight");
        appSettings.Remove("Tenants:tenant-delta-haul");

        var ex = Assert.Throws<InvalidOperationException>(() => FreightRagSettings.Load(Config(appSettings), Secrets()));
        Assert.Contains("Tenants", ex.Message);
    }

    [Fact]
    public void EnvironmentOverrides_reads_the_host_names_by_their_double_underscore_names()
    {
        var env = new Dictionary<string, string?>
        {
            ["Database__Host"] = "db",
            ["Database__Port"] = " 5432 ",
            ["Ollama__Url"] = "http://ollama:11434",
        };

        var overrides = FreightRagSettings.EnvironmentOverrides(name => env.GetValueOrDefault(name));

        Assert.Equal("db", overrides["Database:Host"]);
        Assert.Equal("5432", overrides["Database:Port"]);
        Assert.Equal("http://ollama:11434", overrides["Ollama:Url"]);
        Assert.Equal(3, overrides.Count);
    }

    [Fact]
    public void EnvironmentOverrides_never_reads_the_password_or_unlisted_keys()
    {
        var env = new Dictionary<string, string?>
        {
            ["Database__Password"] = Secret,
            ["Tenants__tenant-nile-freight"] = "de1c81e9-4aa5-4f07-87bd-c5d6f48ff097",
        };

        var overrides = FreightRagSettings.EnvironmentOverrides(name => env.GetValueOrDefault(name));

        Assert.Empty(overrides);
    }

    [Fact]
    public void EnvironmentOverrides_ignores_blank_values()
    {
        var overrides = FreightRagSettings.EnvironmentOverrides(_ => "  ");

        Assert.Empty(overrides);
    }
}

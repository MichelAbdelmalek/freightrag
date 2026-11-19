using FreightRag.Api.Security;
using FreightRag.Core.Infrastructure.Tenants;
using FreightRag.Tests.Support;
using Microsoft.Extensions.Configuration;

namespace FreightRag.Tests.Api;

public class ApiKeyStoreTests
{
    private static readonly string NileKey = TestSecrets.NewFakeSecret();
    private static readonly ConfiguredTenantDirectory Tenants = new(new Dictionary<string, Guid>
    {
        ["tenant-nile-freight"] = Guid.Parse("de1c81e9-4aa5-4f07-87bd-c5d6f48ff097"),
        ["tenant-delta-haul"] = Guid.Parse("14a2de5d-56c5-4eda-a737-d957f4580b90"),
    });

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static readonly IConfiguration NoNonSecretKeys = Config([]);

    private static ApiKeyStore Store(Dictionary<string, string?> secrets) => new(Config(secrets), NoNonSecretKeys, Tenants);

    [Fact]
    public void Resolves_a_valid_key_to_its_tenant()
    {
        var store = Store(new() { ["ApiKeys:tenant-nile-freight"] = NileKey });

        Assert.True(store.TryResolveTenant(NileKey, out var slug));
        Assert.Equal("tenant-nile-freight", slug);
    }

    public static TheoryData<string> WrongKeys() => new()
    {
        "",
        TestSecrets.NewFakeSecret(),   // a different key
        NileKey[..^1],                 // one character short
        NileKey.ToUpperInvariant(),    // keys are case-sensitive
    };

    [Theory]
    [MemberData(nameof(WrongKeys))]
    public void Rejects_unknown_keys(string presented)
    {
        var store = Store(new() { ["ApiKeys:tenant-nile-freight"] = NileKey });

        Assert.False(store.TryResolveTenant(presented, out var slug));
        Assert.Null(slug);
    }

    [Fact]
    public void A_key_for_a_tenant_that_is_not_configured_is_a_startup_error()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Store(new() { ["ApiKeys:tenant-ghost"] = NileKey }));
        Assert.Contains("tenant-ghost", ex.Message);
        Assert.DoesNotContain(NileKey, ex.Message);
    }

    [Fact]
    public void Short_keys_are_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Store(new() { ["ApiKeys:tenant-nile-freight"] = "short" }));
        Assert.Contains($"{ApiKeyStore.MinKeyLength}", ex.Message);
        Assert.DoesNotContain("short\"", ex.Message);
    }

    [Fact]
    public void Two_tenants_cannot_share_a_key()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Store(new()
        {
            ["ApiKeys:tenant-nile-freight"] = NileKey,
            ["ApiKeys:tenant-delta-haul"] = NileKey,
        }));
        Assert.DoesNotContain(NileKey, ex.Message);
    }

    [Fact]
    public void No_keys_at_all_is_a_startup_error()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Store([]));
        Assert.Contains("user-secrets", ex.Message);
    }

    [Fact]
    public void Keys_in_appsettings_or_environment_variables_are_refused()
    {
        var nonSecret = Config(new() { ["ApiKeys:tenant-nile-freight"] = NileKey });

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ApiKeyStore(Config(new() { ["ApiKeys:tenant-nile-freight"] = NileKey }), nonSecret, Tenants));
        Assert.Contains("appsettings.json", ex.Message);
        Assert.DoesNotContain(NileKey, ex.Message);
    }
}

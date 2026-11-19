using FreightRag.Core.Domain;
using FreightRag.Core.Infrastructure.Tenants;

namespace FreightRag.Tests.Infrastructure;

public class ConfiguredTenantDirectoryTests
{
    private static readonly Guid NileGuid = Guid.Parse("de1c81e9-4aa5-4f07-87bd-c5d6f48ff097");

    [Fact]
    public void Resolves_a_mapped_slug_to_its_fixed_guid()
    {
        var directory = new ConfiguredTenantDirectory(new Dictionary<string, Guid> { ["tenant-nile-freight"] = NileGuid });

        Assert.True(directory.TryResolve("tenant-nile-freight", out var tenantId));
        Assert.Equal(new TenantId(NileGuid), tenantId);
    }

    [Theory]
    [InlineData("tenant-unknown")]
    [InlineData("TENANT-NILE-FREIGHT")] // slugs are matched exactly
    [InlineData("")]
    public void Does_not_resolve_unmapped_slugs(string slug)
    {
        var directory = new ConfiguredTenantDirectory(new Dictionary<string, Guid> { ["tenant-nile-freight"] = NileGuid });

        Assert.False(directory.TryResolve(slug, out var tenantId));
        Assert.Null(tenantId);
    }

    [Fact]
    public void Refuses_configuration_mapping_a_slug_to_an_empty_guid()
    {
        Assert.Throws<DomainException>(
            () => new ConfiguredTenantDirectory(new Dictionary<string, Guid> { ["tenant-x"] = Guid.Empty }));
    }

    [Fact]
    public void Refuses_configuration_mapping_two_slugs_to_the_same_guid()
    {
        var ex = Assert.Throws<ArgumentException>(() => new ConfiguredTenantDirectory(
            new Dictionary<string, Guid> { ["tenant-a"] = NileGuid, ["tenant-b"] = NileGuid }));
        Assert.Contains("tenant-a", ex.Message);
        Assert.Contains("tenant-b", ex.Message);
    }
}

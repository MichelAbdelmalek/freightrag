using FreightRag.Core.Domain;

namespace FreightRag.Tests.Domain;

public class TenantIdTests
{
    [Fact]
    public void Rejects_empty_guid()
    {
        Assert.Throws<DomainException>(() => new TenantId(Guid.Empty));
    }

    [Fact]
    public void Two_tenant_ids_with_the_same_guid_are_equal()
    {
        var guid = Guid.Parse("de1c81e9-4aa5-4f07-87bd-c5d6f48ff097");
        Assert.Equal(new TenantId(guid), new TenantId(guid));
    }
}

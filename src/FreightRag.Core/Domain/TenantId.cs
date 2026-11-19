namespace FreightRag.Core.Domain;

/// <summary>
/// Value object identifying a tenant (a carrier company whose documents we store).
/// Every row in the database carries one, which is how tenants are kept apart.
/// A class (not a struct) so that an "empty" default instance can never exist.
/// </summary>
public sealed record TenantId
{
    public TenantId(Guid value)
    {
        if (value == Guid.Empty)
            throw new DomainException("Tenant id cannot be empty.");
        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString();
}

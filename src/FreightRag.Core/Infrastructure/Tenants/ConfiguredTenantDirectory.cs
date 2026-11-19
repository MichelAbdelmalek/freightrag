using System.Diagnostics.CodeAnalysis;
using FreightRag.Core.Application.Ports;
using FreightRag.Core.Domain;

namespace FreightRag.Core.Infrastructure.Tenants;

/// <summary>Tenant directory backed by the "Tenants" section of appsettings.json (slug → fixed GUID).</summary>
public sealed class ConfiguredTenantDirectory : ITenantDirectory
{
    private readonly Dictionary<string, TenantId> _tenants;

    public ConfiguredTenantDirectory(IReadOnlyDictionary<string, Guid> slugToGuid)
    {
        ArgumentNullException.ThrowIfNull(slugToGuid);

        // Fail fast on a copy/paste mistake that would merge two tenants' data.
        var duplicate = slugToGuid.GroupBy(kv => kv.Value).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException(
                "Each tenant needs its own GUID, but these slugs share one: "
                + string.Join(", ", duplicate.Select(kv => kv.Key).Order()) + ".", nameof(slugToGuid));

        // Ordinal: slugs must match the folder name exactly. new TenantId(...) rejects Guid.Empty.
        _tenants = slugToGuid.ToDictionary(kv => kv.Key, kv => new TenantId(kv.Value), StringComparer.Ordinal);
    }

    public bool TryResolve(string tenantSlug, [NotNullWhen(true)] out TenantId? tenantId) =>
        _tenants.TryGetValue(tenantSlug, out tenantId);
}

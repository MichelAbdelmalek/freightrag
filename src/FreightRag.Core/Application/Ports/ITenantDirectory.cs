using System.Diagnostics.CodeAnalysis;
using FreightRag.Core.Domain;

namespace FreightRag.Core.Application.Ports;

/// <summary>Port: maps a tenant slug (the data sub-folder name) to its fixed tenant id.</summary>
public interface ITenantDirectory
{
    bool TryResolve(string tenantSlug, [NotNullWhen(true)] out TenantId? tenantId);
}

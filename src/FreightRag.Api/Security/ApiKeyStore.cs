using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using FreightRag.Core.Application.Ports;

namespace FreightRag.Api.Security;

/// <summary>
/// Maps API keys to tenants. Keys come from User Secrets as <c>ApiKeys:&lt;tenant-slug&gt; = &lt;key&gt;</c>.
/// Only SHA-256 hashes are kept in memory, and lookups compare hashes in constant time.
/// </summary>
public sealed class ApiKeyStore
{
    /// <summary>Minimum key length; `openssl rand -base64 32` produces 44 characters.</summary>
    public const int MinKeyLength = 32;

    private const string Section = "ApiKeys";

    private readonly List<(byte[] Hash, string TenantSlug)> _keys = [];

    /// <param name="userSecrets">The only allowed source of keys.</param>
    /// <param name="nonSecret">appsettings.json + environment variables; must contain no keys.</param>
    /// <param name="tenants">Every key must belong to a configured tenant.</param>
    public ApiKeyStore(IConfiguration userSecrets, IConfiguration nonSecret, ITenantDirectory tenants)
    {
        // Never echo a key in an error message: they are secrets.
        if (nonSecret.GetSection(Section).GetChildren().Any())
            throw new InvalidOperationException(
                $"{Section} must not be set in appsettings.json or environment variables. Store them with .NET User Secrets (see README).");

        foreach (var entry in userSecrets.GetSection(Section).GetChildren())
        {
            var slug = entry.Key;
            if (!tenants.TryResolve(slug, out _))
                throw new InvalidOperationException($"{Section}:{slug} refers to a tenant that is not in the Tenants section of appsettings.json.");
            if (entry.Value is null || entry.Value.Length < MinKeyLength)
                throw new InvalidOperationException($"{Section}:{slug} must be at least {MinKeyLength} characters (e.g. `openssl rand -base64 32`).");

            var hash = Hash(entry.Value);
            if (_keys.Any(k => CryptographicOperations.FixedTimeEquals(k.Hash, hash)))
                throw new InvalidOperationException($"{Section}:{slug} reuses another tenant's key; every tenant needs its own key.");
            _keys.Add((hash, slug));
        }

        if (_keys.Count == 0)
            throw new InvalidOperationException(
                $"No API keys configured. Add one per tenant with dotnet user-secrets ({Section}:<tenant-slug>), see README.");
    }

    /// <summary>Finds the tenant for a presented key. Always checks every key (no early exit), so timing reveals nothing.</summary>
    public bool TryResolveTenant(string presentedKey, [NotNullWhen(true)] out string? tenantSlug)
    {
        tenantSlug = null;
        if (string.IsNullOrEmpty(presentedKey))
            return false;

        var presented = Hash(presentedKey);
        foreach (var (hash, slug) in _keys)
            if (CryptographicOperations.FixedTimeEquals(presented, hash))
                tenantSlug = slug;
        return tenantSlug is not null;
    }

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}

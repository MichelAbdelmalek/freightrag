using Microsoft.Extensions.Configuration;
using Npgsql;

namespace FreightRag.Core.Infrastructure.Configuration;

/// <summary>Database location (from appsettings.json) plus the password (from User Secrets only).</summary>
public sealed class DatabaseSettings(string host, int port, string name, string username, string connectionString)
{
    public string Host { get; } = host;
    public int Port { get; } = port;
    public string Name { get; } = name;
    public string Username { get; } = username;

    /// <summary>Contains the password. Pass it to Npgsql; never log it.</summary>
    public string ConnectionString { get; } = connectionString;

    /// <summary>Safe to log: no password.</summary>
    public string Describe() => $"{Host}:{Port}/{Name} as {Username}";

    /// <summary>Overridden so that accidentally logging this object can't leak the connection string.</summary>
    public override string ToString() => Describe();
}

public sealed record OllamaSettings(Uri Url, string EmbeddingModel);

/// <summary>
/// Settings shared by every FreightRAG host (console ingest, HTTP API), validated up front so
/// misconfiguration fails before any work.
/// </summary>
public sealed record FreightRagSettings(DatabaseSettings Database, OllamaSettings Ollama, IReadOnlyDictionary<string, Guid> Tenants)
{
    private const string PasswordKey = "Database:Password";

    private const string SetPasswordHelp =
        "Store it with .NET User Secrets, passing it on stdin: "
        + "printf '{\"Database:Password\":\"%s\"}' \"$PW\" | docker compose --profile tools run --rm -T user-secrets set "
        + "(see README, step 1).";

    /// <summary>The only settings that may be overridden from the environment (Docker Compose sets them to service names).</summary>
    private static readonly string[] EnvironmentOverridable =
        ["Database:Host", "Database:Port", "Database:Name", "Database:Username", "Ollama:Url", "Ollama:EmbeddingModel"];

    /// <summary>
    /// Reads the overridable keys from environment variables (<c>Database__Host</c> style) so the console hosts can run
    /// in Docker Compose against <c>db</c> and <c>ollama</c>. The password is deliberately not in the list: it can only
    /// come from User Secrets.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> EnvironmentOverrides(Func<string, string?> getEnvironmentVariable)
    {
        var overrides = new Dictionary<string, string?>();
        foreach (var key in EnvironmentOverridable)
        {
            var value = getEnvironmentVariable(key.Replace(":", "__", StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(value))
                overrides[key] = value.Trim();
        }
        return overrides;
    }

    /// <param name="appSettings">Non-secret configuration (appsettings.json, and environment variables for hosts that add them).</param>
    /// <param name="userSecrets">User Secrets only — the single allowed source of the password.</param>
    /// <param name="applicationName">Shown in Postgres (pg_stat_activity) to tell the hosts apart.</param>
    public static FreightRagSettings Load(IConfiguration appSettings, IConfiguration userSecrets, string applicationName = "FreightRag")
    {
        // The password must not be committed with appsettings.json, nor passed as an environment variable
        // (visible in `docker inspect` and process listings).
        if (!string.IsNullOrEmpty(appSettings[PasswordKey]))
            throw new InvalidOperationException(
                $"{PasswordKey} must not be set in appsettings.json or environment variables. Remove it. {SetPasswordHelp}");

        var missing = new List<string>();
        string Required(string key)
        {
            var value = appSettings[key];
            if (string.IsNullOrWhiteSpace(value))
                missing.Add(key);
            return value?.Trim() ?? "";
        }

        var host = Required("Database:Host");
        var portText = Required("Database:Port");
        var name = Required("Database:Name");
        var username = Required("Database:Username");
        var ollamaUrl = Required("Ollama:Url");
        var model = Required("Ollama:EmbeddingModel");
        if (missing.Count > 0)
            throw new InvalidOperationException("Missing required setting(s) in appsettings.json: " + string.Join(", ", missing));

        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException("Database:Port must be a number between 1 and 65535.");

        if (!Uri.TryCreate(ollamaUrl, UriKind.Absolute, out var ollamaUri) || ollamaUri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Ollama:Url must be an absolute http(s) URL.");

        var tenants = appSettings.GetSection("Tenants").Get<Dictionary<string, Guid>>();
        if (tenants is null || tenants.Count == 0)
            throw new InvalidOperationException("No tenant mappings found: add slug → GUID pairs to the Tenants section of appsettings.json.");

        var password = userSecrets[PasswordKey];
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException($"{PasswordKey} is not set. {SetPasswordHelp}");

        // The builder escapes every value correctly (no string concatenation of secrets).
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = name,
            Username = username,
            Password = password,
            ApplicationName = applicationName,
            // We authenticate with a password, not Kerberos. Without this, Npgsql probes for GSS
            // encryption and logs "Cannot load library libgssapi_krb5.so.2" in the slim runtime image.
            GssEncryptionMode = GssEncryptionMode.Disable,
            Timeout = 15,         // seconds to wait for a connection
            CommandTimeout = 60,  // seconds per command
        }.ConnectionString;

        return new FreightRagSettings(
            new DatabaseSettings(host, port, name, username, connectionString),
            new OllamaSettings(ollamaUri, model),
            tenants);
    }
}

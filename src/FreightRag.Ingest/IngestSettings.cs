using Microsoft.Extensions.Configuration;
using Npgsql;

namespace FreightRag.Ingest;

/// <summary>Database location (from appsettings.json) plus the password (from User Secrets only).</summary>
internal sealed class DatabaseSettings(string host, int port, string name, string username, string connectionString)
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

internal sealed record OllamaSettings(Uri Url, string EmbeddingModel);

/// <summary>All settings for one ingest run, validated up front so misconfiguration fails before any work.</summary>
internal sealed record IngestSettings(DatabaseSettings Database, OllamaSettings Ollama, IReadOnlyDictionary<string, Guid> Tenants)
{
    private const string PasswordKey = "Database:Password";

    private const string SetPasswordHelp =
        "Store it with .NET User Secrets, passing it on stdin: "
        + "printf '{\"Database:Password\":\"%s\"}' \"$PW\" | docker compose --profile tools run --rm -T user-secrets set "
        + "(see README, step 1).";

    /// <param name="appSettings">appsettings.json only.</param>
    /// <param name="userSecrets">User Secrets only — the single allowed source of the password.</param>
    public static IngestSettings Load(IConfiguration appSettings, IConfiguration userSecrets)
    {
        // The password must not be committed to source control with appsettings.json.
        if (!string.IsNullOrEmpty(appSettings[PasswordKey]))
            throw new InvalidOperationException(
                $"{PasswordKey} must not be set in appsettings.json. Remove it. {SetPasswordHelp}");

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
            ApplicationName = "FreightRag.Ingest",
            // We authenticate with a password, not Kerberos. Without this, Npgsql probes for GSS
            // encryption and logs "Cannot load library libgssapi_krb5.so.2" in the slim runtime image.
            GssEncryptionMode = GssEncryptionMode.Disable,
            Timeout = 15,         // seconds to wait for a connection
            CommandTimeout = 60,  // seconds per command
        }.ConnectionString;

        return new IngestSettings(
            new DatabaseSettings(host, port, name, username, connectionString),
            new OllamaSettings(ollamaUri, model),
            tenants);
    }
}

/// <summary>Minimal argument parsing: the only argument is --data-dir.</summary>
internal static class CommandLine
{
    private const string Usage = "Usage: FreightRag.Ingest --data-dir <path>. The --data-dir argument is required.";

    public static string ParseDataDir(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            string? value = null;
            if (args[i] == "--data-dir")
                value = i + 1 < args.Length ? args[i + 1] : null;
            else if (args[i].StartsWith("--data-dir=", StringComparison.Ordinal))
                value = args[i]["--data-dir=".Length..];
            else
                continue;

            return string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(Usage) : value;
        }
        throw new ArgumentException(Usage);
    }
}

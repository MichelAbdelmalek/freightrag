namespace FreightRag.Api.Configuration;

/// <summary>
/// Configuration that comes ONLY from .NET User Secrets: the database password and the API keys.
/// Kept apart from <see cref="IConfiguration"/> (appsettings + environment variables) so secrets can't
/// arrive through, or leak into, the ordinary configuration. Tests replace this with in-memory values.
/// </summary>
public sealed record SecretConfiguration(IConfiguration Values);

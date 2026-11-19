using System.Security.Cryptography;

namespace FreightRag.Tests.Support;

/// <summary>
/// Fake passwords and API keys for tests, generated at runtime. Keeping credential-looking literals out
/// of the source means secret scanners (GitGuardian, GitHub secret scanning) have nothing to flag, and
/// nobody can mistake a test value for a real one.
/// </summary>
internal static class TestSecrets
{
    /// <summary>A random 44-character value (same shape as `openssl rand -base64 32`).</summary>
    public static string NewFakeSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}

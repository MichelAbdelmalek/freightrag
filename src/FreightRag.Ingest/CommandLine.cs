namespace FreightRag.Ingest;

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

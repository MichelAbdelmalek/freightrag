// FreightRag.Migrator — applies the EF Core migrations to the database (code-first schema).
//
//   docker compose run --rm migrate
//
// Reads the same appsettings.json + User Secrets as the other hosts. The configured embedding model name
// (Ollama:EmbeddingModel) is used to backfill chunks.embedding_model for existing rows.
// Exit codes: 0 = up to date, 1 = migration failed, 2 = invalid configuration.

using FreightRag.Core.Domain;
using FreightRag.Core.Infrastructure.Configuration;
using FreightRag.Core.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

using var loggerFactory = LoggerFactory.Create(logging => logging
    .SetMinimumLevel(LogLevel.Information)
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; }));
var logger = loggerFactory.CreateLogger("FreightRag.Migrator");

FreightRagSettings settings;
try
{
    var appSettings = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .Build();
    var userSecrets = new ConfigurationBuilder().AddUserSecrets(typeof(Program).Assembly, optional: true).Build();
    settings = FreightRagSettings.Load(appSettings, userSecrets, applicationName: "FreightRag.Migrator");
}
catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException or FileNotFoundException)
{
    logger.LogError("{Message}", ex.Message);
    return 2;
}

logger.LogInformation("Database: {Database}", settings.Database.Describe()); // never the connection string

try
{
    await using var dataSource = FreightRagDbContextOptions.CreateDataSource(settings.Database.ConnectionString);
    await using var db = new FreightRagDbContext(FreightRagDbContextOptions.Create(dataSource));

    var report = await new DatabaseMigrator(db).MigrateAsync(new EmbeddingModelName(settings.Ollama.EmbeddingModel));

    if (report.Baselined)
        logger.LogInformation("Adopted an existing pre-EF database: recorded InitialSchema as already applied");
    if (report.Applied.Count == 0)
        logger.LogInformation("Database schema is up to date");
    foreach (var migration in report.Applied)
        logger.LogInformation("Applied migration {Migration}", migration);
    return 0;
}
catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or DomainException)
{
    logger.LogError(ex, "Migration failed: {Message}", ex.Message);
    return 1;
}

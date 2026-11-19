using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace FreightRag.Core.Infrastructure.Persistence;

/// <summary>
/// Generates the reviewable db/NNN_name.sql scripts from the EF Core migrations, and the SQL that adopts a
/// database created before EF Core (used by those scripts and by <see cref="DatabaseMigrator"/>).
/// </summary>
public static partial class DbScripts
{
    /// <summary>"20261119173527_AddEmbeddingModel" → "AddEmbeddingModel".</summary>
    public static string MigrationName(string migrationId) => migrationId[(migrationId.IndexOf('_') + 1)..];

    /// <summary>One script per migration after InitialSchema: file name (e.g. "002_add_embedding_model.sql") → content.</summary>
    public static IReadOnlyDictionary<string, string> GenerateAll(DbContext db)
    {
        var migrations = db.Database.GetMigrations().ToList();
        var migrator = db.GetService<IMigrator>();
        var scripts = new Dictionary<string, string>();
        for (var i = 1; i < migrations.Count; i++)
        {
            var name = MigrationName(migrations[i]);
            var script = migrator.GenerateScript(migrations[i - 1], migrations[i], MigrationsSqlGenerationOptions.Idempotent);
            scripts[$"{i + 1:000}_{SnakeCase(name)}.sql"] =
                Header(name) + AdoptPreEfDatabaseSql(db) + "\n" + script.ReplaceLineEndings("\n");
        }
        return scripts;
    }

    /// <summary>
    /// Idempotent SQL that records InitialSchema as applied on a database created by db/001_schema.sql (tables exist,
    /// no or an empty migrations history). Does nothing on an EF-managed database. Literals are the migration id and
    /// EF version — constants, never input.
    /// </summary>
    public static string AdoptPreEfDatabaseSql(DbContext db)
    {
        var history = db.GetService<IHistoryRepository>();
        var initial = db.Database.GetMigrations().First();
        return $"""
            -- Adopt a database created before EF Core (db/001_schema.sql): record InitialSchema as already applied.
            {history.GetCreateIfNotExistsScript().Trim()}

            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            SELECT '{initial}', '{ProductInfo.GetVersion()}'
            WHERE to_regclass('public.documents') IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '{initial}');

            """;
    }

    public static string Header(string migrationName) => $"""
        -- GENERATED from the EF Core migration "{migrationName}" (src/FreightRag.Core/Infrastructure/Persistence/Migrations).
        -- Do not edit: the C# migration is the source of truth. This idempotent script exists so the change can be
        -- reviewed (or applied by hand by a DBA). Normal path: `docker compose run --rm migrate`.
        -- Works on an EF-managed database and on one created by db/001_schema.sql (adopted below).
        -- Backfill note: set the session setting freightrag.embedding_model to the configured model first, e.g.
        --   SELECT set_config('freightrag.embedding_model', 'nomic-embed-text', false);

        """;

    private static string SnakeCase(string pascal) => UpperAfterLower().Replace(pascal, "$1_$2").ToLowerInvariant();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex UpperAfterLower();
}

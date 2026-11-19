using System.Text.RegularExpressions;
using FreightRag.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace FreightRag.Tests.Infrastructure.Persistence;

/// <summary>
/// The EF Core migrations are the source of truth for the schema. Every migration after InitialSchema has a
/// generated, reviewable script db/NNN_snake_name.sql (NNN = position, 001 is the original hand-written schema).
/// These tests fail when a script is missing, extra, or drifts from its migration.
/// Regenerate with UPDATE_DB_SCRIPTS=1 (bind-mounted test run, see README).
/// </summary>
public partial class MigrationScriptTests
{
    [Fact]
    public void The_first_migration_is_InitialSchema_and_AddEmbeddingModel_follows()
    {
        using var db = DesignTimeContext();
        var names = db.Database.GetMigrations().Select(DbScripts.MigrationName).ToList();

        Assert.Equal("InitialSchema", names[0]);
        Assert.Contains("AddEmbeddingModel", names);
    }

    [Fact]
    public void The_model_has_no_changes_missing_from_the_migrations()
    {
        // Same check as `dotnet ef migrations has-pending-model-changes`.
        using var db = DesignTimeContext();

        Assert.False(db.Database.HasPendingModelChanges(), "Model changed without a migration: run `dotnet ef migrations add <Name>`.");
    }

    [Fact]
    public void Every_migration_after_InitialSchema_has_an_up_to_date_script_in_db()
    {
        using var db = DesignTimeContext();
        var expected = DbScripts.GenerateAll(db);
        var dbDir = Path.Combine(RepoRoot(), "db");

        if (Environment.GetEnvironmentVariable("UPDATE_DB_SCRIPTS") == "1")
            foreach (var (file, content) in expected)
                File.WriteAllText(Path.Combine(dbDir, file), content);

        // No missing and no extra generated scripts (001_schema.sql is the hand-written original).
        var present = Directory.GetFiles(dbDir, "*.sql").Select(Path.GetFileName).Where(f => f != "001_schema.sql").Order();
        Assert.Equal(expected.Keys.Order(), present);

        foreach (var (file, content) in expected)
        {
            var committed = File.ReadAllText(Path.Combine(dbDir, file)).ReplaceLineEndings("\n");
            Assert.True(MaskEfVersion(committed) == MaskEfVersion(content),
                $"db/{file} differs from its migration. Re-run the tests with UPDATE_DB_SCRIPTS=1 and commit the file.");
        }
    }

    [Fact]
    public void Scripts_start_with_the_idempotent_adoption_of_a_pre_EF_database()
    {
        // So the script also works on a database created by db/001_schema.sql (no migrations history yet).
        using var db = DesignTimeContext();
        var script = DbScripts.GenerateAll(db)["002_add_embedding_model.sql"];

        var preamble = script.IndexOf("CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\"", StringComparison.Ordinal);
        Assert.True(preamble > 0 && preamble < script.IndexOf("START TRANSACTION", StringComparison.Ordinal));
        Assert.Contains("to_regclass('public.documents') IS NOT NULL", script);
    }

    [Fact]
    public void Comparison_ignores_the_EF_Core_version_stamp()
    {
        // EF writes its own version into the history rows; a Dependabot patch bump must not fail CI.
        Assert.Equal(
            MaskEfVersion("""VALUES ('20261119173527_AddEmbeddingModel', '10.0.14');"""),
            MaskEfVersion("""VALUES ('20261119173527_AddEmbeddingModel', '10.0.15');"""));
    }

    private static string MaskEfVersion(string sql) => EfVersion().Replace(sql, "$1'<ef-version>'");

    [GeneratedRegex(@"(_\w+', )'\d+\.\d+\.\d+[^']*'")]
    private static partial Regex EfVersion();

    private static FreightRagDbContext DesignTimeContext() => new DesignTimeDbContextFactory().CreateDbContext([]);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "FreightRag.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Could not find the repository root (src/FreightRag.slnx).");
    }
}

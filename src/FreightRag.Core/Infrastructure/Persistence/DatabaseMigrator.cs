using FreightRag.Core.Domain;
using FreightRag.Core.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace FreightRag.Core.Infrastructure.Persistence;

/// <summary>What a migration run did.</summary>
/// <param name="Baselined">True if a database created before EF Core (by db/001_schema.sql) was adopted.</param>
/// <param name="Applied">Migrations applied by this run, in order.</param>
public sealed record MigrationReport(bool Baselined, IReadOnlyList<string> Applied);

/// <summary>
/// Applies the EF Core migrations. Used by the FreightRag.Migrator console and the integration tests,
/// never at application startup (schema changes are an explicit, reviewable step).
/// </summary>
public sealed class DatabaseMigrator(FreightRagDbContext db)
{
    public async Task<MigrationReport> MigrateAsync(
        EmbeddingModelName embeddingModel, string? targetMigration = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embeddingModel);

        // One open connection for the whole run, so the session setting below is visible to the migrations.
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            // The AddEmbeddingModel backfill reads the configured model name from this session setting.
            // set_config() takes parameters, so the name is never concatenated into SQL.
            await db.Database.ExecuteSqlAsync(
                $"SELECT set_config({AddEmbeddingModel.EmbeddingModelSetting}, {embeddingModel.Value}, false)", cancellationToken);

            var baselined = await BaselineIfNeededAsync(cancellationToken);
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (targetMigration is not null)
            {
                var target = pending.FindIndex(m => m.EndsWith("_" + targetMigration, StringComparison.Ordinal));
                pending = target < 0 ? [] : pending[..(target + 1)];
            }

            await db.GetService<IMigrator>().MigrateAsync(
                targetMigration is null ? null : db.Database.GetMigrations().Single(m => m.EndsWith("_" + targetMigration, StringComparison.Ordinal)),
                cancellationToken: cancellationToken);
            return new MigrationReport(baselined, pending);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// A database created by db/001_schema.sql (before EF Core) has the InitialSchema tables but no — or an empty —
    /// migrations history. Record InitialSchema as applied so EF continues from there instead of failing on
    /// "relation already exists". Runs in one transaction under an advisory lock, so a concurrent or interrupted
    /// run can't leave a half-adopted database behind. Uses the same SQL as the generated db/*.sql scripts.
    /// </summary>
    private async Task<bool> BaselineIfNeededAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({BaselineLockKey})", cancellationToken);

        var initial = db.Database.GetMigrations().First();
        var hasTables = await Scalar("SELECT to_regclass('public.documents') IS NOT NULL AS \"Value\"", cancellationToken);
        var hasHistory = await Scalar("SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL AS \"Value\"", cancellationToken);
        var initialRecorded = hasHistory && await db.Database
            .SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = {initial}) AS \"Value\"")
            .SingleAsync(cancellationToken);

        var baseline = hasTables && !initialRecorded;
        if (baseline)
            await db.Database.ExecuteSqlRawAsync(DbScripts.AdoptPreEfDatabaseSql(db), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return baseline;
    }

    /// <summary>Arbitrary constant identifying FreightRAG's baseline lock (pg_advisory_xact_lock key).</summary>
    private const long BaselineLockKey = 4_717_350_219_001;

    private Task<bool> Scalar(string sql, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<bool>(sql).SingleAsync(cancellationToken);
}

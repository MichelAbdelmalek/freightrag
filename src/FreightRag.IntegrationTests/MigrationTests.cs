using FreightRag.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FreightRag.IntegrationTests;

/// <summary>The code-first migrations against a real database.</summary>
public sealed class MigrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InitialSchema_builds_exactly_the_schema_of_db_001_schema_sql()
    {
        await using var fromSql = await TestDatabase.CreateAsync(migrate: false);
        await fromSql.ExecuteScriptAsync(await File.ReadAllTextAsync(TestDatabase.ScriptPath("001_schema.sql"), Ct));
        await using var fromMigrations = await TestDatabase.CreateAsync(migrateTo: "InitialSchema");

        Assert.Equal(await DescribeSchema(fromSql), await DescribeSchema(fromMigrations));
    }

    [Fact]
    public async Task A_database_created_before_EF_is_baselined_and_backfilled_with_the_configured_model()
    {
        await using var database = await TestDatabase.CreateAsync(migrate: false);
        await database.ExecuteScriptAsync(await File.ReadAllTextAsync(TestDatabase.ScriptPath("001_schema.sql"), Ct));
        await InsertPreMigrationChunk(database);

        await using var db = database.Contexts.CreateDbContext();
        var report = await new DatabaseMigrator(db).MigrateAsync(TestDatabase.Model, cancellationToken: Ct);

        Assert.True(report.Baselined);
        Assert.Equal(["AddEmbeddingModel", "AddContentFullTextSearch"], report.Applied.Select(m => m[(m.IndexOf('_') + 1)..]));
        Assert.Equal(["nomic-embed-text"], await Scalars(database, "SELECT embedding_model FROM chunks"));
        Assert.Equal(["NO"], await Scalars(database,
            "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'chunks' AND column_name = 'embedding_model'"));
    }

    [Fact]
    public async Task The_generated_db_scripts_work_on_a_database_created_by_db_001()
    {
        // The case the script exists for: a pre-EF database (like the original dev database), no migrations history.
        await using var database = await TestDatabase.CreateAsync(migrate: false);
        await database.ExecuteScriptAsync(await File.ReadAllTextAsync(TestDatabase.ScriptPath("001_schema.sql"), Ct));
        await InsertPreMigrationChunk(database);

        await RunScriptWithModel(database, "002_add_embedding_model.sql");
        await RunScriptWithModel(database, "003_add_content_full_text_search.sql");

        Assert.Equal(["nomic-embed-text"], await Scalars(database, "SELECT embedding_model FROM chunks"));
        Assert.Equal(["true"], await Scalars(database, "SELECT (content_tsv = to_tsvector('english', content))::text FROM chunks"));
        await using var db = database.Contexts.CreateDbContext();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct)); // InitialSchema was adopted, the later migrations applied
    }

    [Fact]
    public async Task A_half_finished_baseline_is_recovered()
    {
        // An interrupted earlier run left an empty history table behind: the migrator must still adopt the database.
        await using var database = await TestDatabase.CreateAsync(migrate: false);
        await database.ExecuteScriptAsync(await File.ReadAllTextAsync(TestDatabase.ScriptPath("001_schema.sql"), Ct));
        await database.ExecuteScriptAsync("""
            CREATE TABLE "__EFMigrationsHistory" ("MigrationId" character varying(150) PRIMARY KEY, "ProductVersion" character varying(32) NOT NULL)
            """);

        await using var db = database.Contexts.CreateDbContext();
        var report = await new DatabaseMigrator(db).MigrateAsync(TestDatabase.Model, cancellationToken: Ct);

        Assert.True(report.Baselined);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task The_full_text_column_is_filled_for_rows_that_existed_before_the_migration()
    {
        await using var database = await TestDatabase.CreateAsync(migrateTo: "AddEmbeddingModel");
        var vector = "[" + string.Join(",", Vectors.Query()) + "]";
        await database.ExecuteScriptAsync($"""
            INSERT INTO documents (id, tenant_id, title, doc_type, source_hash)
            VALUES ('{Guid.NewGuid()}', '{Seed.TenantA}', 'old-doc', 'invoice', 'hash');
            INSERT INTO chunks (document_id, tenant_id, chunk_index, content, embedding, embedding_model)
            SELECT id, tenant_id, 0, 'Detention: $50.00 per hour after 2 free hours', '{vector}'::vector, 'nomic-embed-text' FROM documents;
            """); // test data only: literals built from constants

        await using var db = database.Contexts.CreateDbContext();
        await new DatabaseMigrator(db).MigrateAsync(TestDatabase.Model, cancellationToken: Ct);

        Assert.Equal(["true"], await Scalars(database, "SELECT (content_tsv = to_tsvector('english', content))::text FROM chunks"));
        Assert.Equal(["'2':6 '50.00':2 'detent':1 'free':7 'hour':4,8 'per':3"], await Scalars(database, "SELECT content_tsv::text FROM chunks"));
    }

    [Fact]
    public async Task Running_the_migrator_again_changes_nothing()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.Contexts.CreateDbContext();

        var report = await new DatabaseMigrator(db).MigrateAsync(TestDatabase.Model, cancellationToken: Ct);

        Assert.False(report.Baselined);
        Assert.Empty(report.Applied);
    }

    [Fact]
    public async Task The_backfill_refuses_to_guess_when_the_model_name_was_not_configured()
    {
        await using var database = await TestDatabase.CreateAsync(migrateTo: "InitialSchema");
        await InsertPreMigrationChunk(database);

        // Plain EF migrate, without the migrator's set_config: rows need a value and none is configured.
        await using var db = database.Contexts.CreateDbContext();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync(Ct));

        Assert.Contains("backfill needs the configured model name", ex.MessageText);
        Assert.Equal([""], await Scalars(database,
            "SELECT coalesce(string_agg(column_name, ','), '') FROM information_schema.columns WHERE table_name = 'chunks' AND column_name = 'embedding_model'"));
    }

    [Fact]
    public async Task The_generated_db_scripts_apply_and_backfill()
    {
        await using var database = await TestDatabase.CreateAsync(migrateTo: "InitialSchema");
        await InsertPreMigrationChunk(database);

        await RunScriptWithModel(database, "002_add_embedding_model.sql");
        await RunScriptWithModel(database, "003_add_content_full_text_search.sql");

        Assert.Equal(["nomic-embed-text"], await Scalars(database, "SELECT embedding_model FROM chunks"));
        Assert.Equal(["true"], await Scalars(database, "SELECT (content_tsv = to_tsvector('english', content))::text FROM chunks"));
        await using var db = database.Contexts.CreateDbContext();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
    }

    /// <summary>What a DBA would do: set the model name for the backfill, then run the generated script.</summary>
    private static async Task RunScriptWithModel(TestDatabase database, string fileName)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(Ct);
        await using var setModel = new NpgsqlCommand("SELECT set_config('freightrag.embedding_model', $1, false)", connection);
        setModel.Parameters.Add(new NpgsqlParameter<string> { TypedValue = "nomic-embed-text" });
        await setModel.ExecuteNonQueryAsync(Ct);
        await using var script = new NpgsqlCommand(await File.ReadAllTextAsync(TestDatabase.ScriptPath(fileName), Ct), connection);
        await script.ExecuteNonQueryAsync(Ct);
    }

    private static async Task InsertPreMigrationChunk(TestDatabase database)
    {
        var vector = "[" + string.Join(",", Vectors.Query()) + "]";
        await using var connection = await database.DataSource.OpenConnectionAsync(Ct);
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO documents (id, tenant_id, title, doc_type, source_hash)
            VALUES ('{Guid.NewGuid()}', '{Seed.TenantA}', 'old-doc', 'invoice', 'hash');
            INSERT INTO chunks (document_id, tenant_id, chunk_index, content, embedding)
            SELECT id, tenant_id, 0, 'old chunk', '{vector}'::vector FROM documents;
            """, connection); // test data only: literals built from constants, no external input
        await insert.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<List<string>> Scalars(TestDatabase database, string sql)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            values.Add(reader.GetValue(0)?.ToString() ?? "");
        return values;
    }

    /// <summary>Columns (with exact types), constraints and indexes of the two tables, as comparable text.</summary>
    private static async Task<List<string>> DescribeSchema(TestDatabase database) => await Scalars(database, """
        SELECT 'column ' || c.relname || '.' || a.attname || ' ' || format_type(a.atttypid, a.atttypmod)
               || CASE WHEN a.attnotnull THEN ' NOT NULL' ELSE '' END
               || coalesce(' DEFAULT ' || pg_get_expr(d.adbin, d.adrelid), '')
        FROM pg_attribute a
        JOIN pg_class c ON c.oid = a.attrelid
        LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
        WHERE c.relname IN ('documents', 'chunks') AND a.attnum > 0 AND NOT a.attisdropped
        UNION ALL
        SELECT 'constraint ' || conname || ' ' || pg_get_constraintdef(oid)
        FROM pg_constraint WHERE conrelid IN ('documents'::regclass, 'chunks'::regclass)
        UNION ALL
        SELECT 'index ' || indexname || ' ' || indexdef
        FROM pg_indexes WHERE schemaname = 'public' AND tablename IN ('documents', 'chunks')
        UNION ALL
        SELECT 'extension ' || extname FROM pg_extension WHERE extname = 'vector'
        ORDER BY 1
        """);
}

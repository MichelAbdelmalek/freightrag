using FreightRag.Core.Domain;
using FreightRag.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FreightRag.IntegrationTests;

/// <summary>
/// A fresh, uniquely named database on the test server, dropped afterwards. Every test gets its own, so tests
/// can run in parallel and never see each other's rows.
///
/// enable_seqscan is switched off for the whole database: with only a few hundred rows PostgreSQL would
/// otherwise prefer a sequential scan, and the HNSW index (the thing these tests are about) would never be used.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    public static readonly EmbeddingModelName Model = new("nomic-embed-text");

    private TestDatabase(string name, NpgsqlDataSource dataSource)
    {
        Name = name;
        DataSource = dataSource;
        Contexts = new FreightRagDbContextFactory(FreightRagDbContextOptions.Create(dataSource));
    }

    public string Name { get; }
    public NpgsqlDataSource DataSource { get; }
    public FreightRagDbContextFactory Contexts { get; }

    /// <summary>A context factory with extra EF Core interceptors (e.g. to inspect the commands EF sends).</summary>
    public FreightRagDbContextFactory ContextsWith(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<FreightRagDbContext>();
        FreightRagDbContextOptions.Configure(options, DataSource);
        options.AddInterceptors(interceptors);
        return new FreightRagDbContextFactory(options.Options);
    }

    /// <summary>Creates the database; by default applies all EF Core migrations (which tests them, too).</summary>
    public static async Task<TestDatabase> CreateAsync(bool migrate = true, string? migrateTo = null)
    {
        var server = ServerConnectionString();
        // Identifiers can't be parameters; the name is generated from a GUID (hex only), never from input.
        var name = "it_" + Guid.NewGuid().ToString("N");

        await using (var admin = new NpgsqlConnection(server))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync();
            await using var noSeqScan = new NpgsqlCommand($"ALTER DATABASE \"{name}\" SET enable_seqscan = off", admin);
            await noSeqScan.ExecuteNonQueryAsync();
        }

        var dataSource = FreightRagDbContextOptions.CreateDataSource(
            new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString);
        var database = new TestDatabase(name, dataSource);

        if (migrate)
        {
            await using var db = database.Contexts.CreateDbContext();
            await new DatabaseMigrator(db).MigrateAsync(Model, migrateTo);
            // The extension's types are created by the migration; reload them so pooled connections see "vector".
            await using var connection = await dataSource.OpenConnectionAsync();
            await connection.ReloadTypesAsync();
        }
        return database;
    }

    /// <summary>Runs a SQL script file (e.g. db/001_schema.sql) as-is.</summary>
    public async Task ExecuteScriptAsync(string sql)
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
        await connection.ReloadTypesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(ServerConnectionString());
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }

    public static string ScriptPath(string fileName) => Path.Combine(AppContext.BaseDirectory, "db", fileName);

    /// <summary>
    /// The test server. Without FREIGHTRAG_TEST_DB the tests are skipped (plain `dotnet test` on a laptop) — unless
    /// FREIGHTRAG_REQUIRE_INTEGRATION=1 (compose and CI), where a missing database is a failure, never a silent pass.
    /// </summary>
    private static string ServerConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("FREIGHTRAG_TEST_DB");
        if (!string.IsNullOrWhiteSpace(connectionString))
            return connectionString;
        if (Environment.GetEnvironmentVariable("FREIGHTRAG_REQUIRE_INTEGRATION") == "1")
            throw new InvalidOperationException("FREIGHTRAG_TEST_DB is not set but integration tests are required.");
        Assert.Skip("FREIGHTRAG_TEST_DB is not set. Run: docker compose run --build --rm integration-tests");
        return "";
    }
}

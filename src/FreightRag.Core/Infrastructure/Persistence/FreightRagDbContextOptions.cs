using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FreightRag.Core.Infrastructure.Persistence;

/// <summary>One place that configures EF Core for Postgres + pgvector, used by every host and by tests.</summary>
public static class FreightRagDbContextOptions
{
    /// <summary>Creates the Npgsql connection pool with pgvector support.</summary>
    public static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        return builder.Build();
    }

    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, NpgsqlDataSource dataSource) =>
        options.UseNpgsql(dataSource, npgsql => npgsql.UseVector());

    public static DbContextOptions<FreightRagDbContext> Create(NpgsqlDataSource dataSource) =>
        (DbContextOptions<FreightRagDbContext>)Configure(new DbContextOptionsBuilder<FreightRagDbContext>(), dataSource).Options;
}

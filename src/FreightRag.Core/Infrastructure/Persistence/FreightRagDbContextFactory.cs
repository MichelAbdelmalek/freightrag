using Microsoft.EntityFrameworkCore;

namespace FreightRag.Core.Infrastructure.Persistence;

/// <summary>Creates short-lived contexts (one per operation) for hosts that don't use DI (console apps, tests).</summary>
public sealed class FreightRagDbContextFactory(DbContextOptions<FreightRagDbContext> options) : IDbContextFactory<FreightRagDbContext>
{
    public FreightRagDbContext CreateDbContext() => new(options);
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FreightRag.Core.Infrastructure.Persistence;

/// <summary>
/// Used only by the <c>dotnet ef</c> tool to create migrations. It never connects: adding a migration
/// compares the model with the last snapshot, so no real database or credentials are needed.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<FreightRagDbContext>
{
    public FreightRagDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<FreightRagDbContext>()
            .UseNpgsql("Host=design-time-only;Database=freightrag", npgsql => npgsql.UseVector())
            .Options);
}

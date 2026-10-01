using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>For <c>dotnet ef migrations add</c> only. Generating a migration needs no running database.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<CooDbContext>
{
    public CooDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CooDbContext>().UseNpgsql("Host=design-time-only").Options);
}

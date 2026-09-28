using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hippo.Migrations;

public sealed class HippoDbContext(DbContextOptions<HippoDbContext> options) : DbContext(options);

public sealed class HippoDbContextFactory : IDesignTimeDbContextFactory<HippoDbContext>
{
    public HippoDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<HippoDbContext>().UseSqlite("Data Source=design-time.db").Options);
}

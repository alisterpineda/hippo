using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hippo.Migrations;

public sealed class HippoDbContext(DbContextOptions<HippoDbContext> options) : DbContext(options)
{
    public DbSet<FileEntity> Files => Set<FileEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FileEntity>(file =>
        {
            file.ToTable("files", table => table.HasCheckConstraint("ck_files_kind", "kind IN ('markdown', 'plain')"));
            file.HasKey(f => f.Id);
            file.Property(f => f.Id).HasColumnName("id");
            file.Property(f => f.Path).HasColumnName("path");
            file.Property(f => f.Mtime).HasColumnName("mtime");
            file.Property(f => f.Size).HasColumnName("size");
            file.Property(f => f.Hash).HasColumnName("hash");
            file.Property(f => f.Kind).HasColumnName("kind");
            file.Property(f => f.Frontmatter).HasColumnName("frontmatter");
            file.Property(f => f.ParseError).HasColumnName("parse_error");
            file.HasIndex(f => f.Path).IsUnique().HasDatabaseName("ix_files_path");
        });
    }
}

public sealed class HippoDbContextFactory : IDesignTimeDbContextFactory<HippoDbContext>
{
    public HippoDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<HippoDbContext>().UseSqlite("Data Source=design-time.db").Options);
}

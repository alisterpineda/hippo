using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hippo.Migrations;

/// <summary>
/// The index schema as an EF Core model, used only to author migrations: <c>scripts/add-migration.sh</c> diffs it against
/// the last snapshot and exports the SQL that hippo embeds. hippo itself reads and writes with Dapper and never loads EF.
/// </summary>
public sealed class HippoDbContext(DbContextOptions<HippoDbContext> options) : DbContext(options)
{
    public DbSet<FileEntity> Files => Set<FileEntity>();

    public DbSet<LinkEntity> Links => Set<LinkEntity>();

    public DbSet<FindingEntity> Findings => Set<FindingEntity>();

    public DbSet<MetaEntity> Meta => Set<MetaEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FileEntity>(file =>
        {
            file.ToTable("files", table => table.HasCheckConstraint("ck_files_kind", "kind IN ('markdown', 'other')"));
            file.HasKey(f => f.Id);
            file.Property(f => f.Id).HasColumnName("id");
            file.Property(f => f.Path).HasColumnName("path");
            file.Property(f => f.Mtime).HasColumnName("mtime");
            file.Property(f => f.Size).HasColumnName("size");
            file.Property(f => f.Hash).HasColumnName("hash");
            file.Property(f => f.HashedAt).HasColumnName("hashed_at");
            file.Property(f => f.Kind).HasColumnName("kind");
            file.Property(f => f.Frontmatter).HasColumnName("frontmatter");
            file.Property(f => f.ParseError).HasColumnName("parse_error");
            file.HasIndex(f => f.Path).IsUnique().HasDatabaseName("ix_files_path");
        });

        modelBuilder.Entity<LinkEntity>(link =>
        {
            link.ToTable("links", table =>
            {
                table.HasCheckConstraint("ck_links_kind", "kind IN ('body', 'frontmatter')");
                table.HasCheckConstraint("ck_links_type", "type IN ('path', 'url', 'anchor')");
                table.HasCheckConstraint("ck_links_target", "type = 'path' OR target IS NULL");
            });
            link.HasKey(l => l.Id);
            // A plain INTEGER PRIMARY KEY, without AUTOINCREMENT: SQLite may reuse the ids of deleted links.
            link.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();
            link.Property(l => l.SourceId).HasColumnName("source_id");
            link.Property(l => l.Line).HasColumnName("line");
            link.Property(l => l.Kind).HasColumnName("kind");
            link.Property(l => l.Type).HasColumnName("type");
            link.Property(l => l.Raw).HasColumnName("raw");
            link.Property(l => l.Target).HasColumnName("target");
            link.HasOne<FileEntity>().WithMany().HasForeignKey(l => l.SourceId).OnDelete(DeleteBehavior.Cascade);
            link.HasIndex(l => l.SourceId).HasDatabaseName("ix_links_source_id");
            link.HasIndex(l => l.Target).HasDatabaseName("ix_links_target");
        });

        modelBuilder.Entity<FindingEntity>(finding =>
        {
            finding.ToTable("findings");
            finding.HasKey(f => f.Id);
            // A plain INTEGER PRIMARY KEY, as for links: findings are rewritten whenever their file is.
            finding.Property(f => f.Id).HasColumnName("id").ValueGeneratedNever();
            finding.Property(f => f.FileId).HasColumnName("file_id");
            finding.Property(f => f.Rule).HasColumnName("rule");
            finding.Property(f => f.Line).HasColumnName("line");
            finding.Property(f => f.Message).HasColumnName("message");
            finding.Property(f => f.Related).HasColumnName("related");
            finding.HasOne<FileEntity>().WithMany().HasForeignKey(f => f.FileId).OnDelete(DeleteBehavior.Cascade);
            finding.HasIndex(f => f.FileId).HasDatabaseName("ix_findings_file_id");
        });

        modelBuilder.Entity<MetaEntity>(meta =>
        {
            meta.ToTable("meta");
            meta.HasKey(m => m.Key);
            meta.Property(m => m.Key).HasColumnName("key");
            meta.Property(m => m.Value).HasColumnName("value");
        });
    }
}

public sealed class HippoDbContextFactory : IDesignTimeDbContextFactory<HippoDbContext>
{
    public HippoDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<HippoDbContext>().UseSqlite("Data Source=design-time.db").Options);
}

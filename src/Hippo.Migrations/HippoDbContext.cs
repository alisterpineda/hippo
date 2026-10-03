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

    public DbSet<IndexEntryEntity> IndexEntries => Set<IndexEntryEntity>();

    public DbSet<MetaEntity> Meta => Set<MetaEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FileEntity>(file =>
        {
            file.ToTable("files", table => table.HasCheckConstraint("ck_files_kind", "kind IN ('markdown', 'other')"));
            file.HasKey(f => f.Id);
            file.Property(f => f.Id).HasColumnName("id");
            file.Property(f => f.Path).HasColumnName("path");
            // SQLite needs a default to add a NOT NULL column to a table with rows; the migration then sets those rows to
            // their path. The default stays on the column, so an INSERT that leaves path_nfd out gets '' and matches no
            // link: every insert must give it.
            file.Property(f => f.PathNfd).HasColumnName("path_nfd").HasDefaultValue("");
            file.Property(f => f.Mtime).HasColumnName("mtime");
            file.Property(f => f.Size).HasColumnName("size");
            file.Property(f => f.Hash).HasColumnName("hash");
            file.Property(f => f.HashedAt).HasColumnName("hashed_at");
            file.Property(f => f.Kind).HasColumnName("kind");
            file.Property(f => f.Frontmatter).HasColumnName("frontmatter");
            file.Property(f => f.ParseError).HasColumnName("parse_error");
            file.HasIndex(f => f.Path).IsUnique().HasDatabaseName("ix_files_path");
            // Not unique: on a filesystem that keeps names apart by form, two files can share one.
            file.HasIndex(f => f.PathNfd).HasDatabaseName("ix_files_path_nfd");
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
            link.Property(l => l.TargetNfd).HasColumnName("target_nfd");
            link.Property(l => l.Text).HasColumnName("text");
            link.HasOne<FileEntity>().WithMany().HasForeignKey(l => l.SourceId).OnDelete(DeleteBehavior.Cascade);
            link.HasIndex(l => l.SourceId).HasDatabaseName("ix_links_source_id");
            link.HasIndex(l => l.TargetNfd).HasDatabaseName("ix_links_target_nfd");
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

        modelBuilder.Entity<IndexEntryEntity>(entry =>
        {
            entry.ToTable("index_entries");
            entry.HasKey(e => e.Id);
            // A plain INTEGER PRIMARY KEY, as for links: entries are rewritten whenever their file is.
            entry.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            entry.Property(e => e.FileId).HasColumnName("file_id");
            entry.Property(e => e.Line).HasColumnName("line");
            entry.Property(e => e.Target).HasColumnName("target");
            // As for files.path_nfd: the migration sets the rows it finds to their target, and every insert must give it.
            entry.Property(e => e.TargetNfd).HasColumnName("target_nfd").HasDefaultValue("");
            entry.Property(e => e.Description).HasColumnName("description");
            entry.HasOne<FileEntity>().WithMany().HasForeignKey(e => e.FileId).OnDelete(DeleteBehavior.Cascade);
            entry.HasIndex(e => e.FileId).HasDatabaseName("ix_index_entries_file_id");
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

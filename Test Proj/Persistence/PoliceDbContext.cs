using Microsoft.EntityFrameworkCore;

namespace Test_Proj.Persistence;

// Data is a canonical, validated DTO projection, never an unvalidated upstream response.
public sealed class PoliceRecord
{
    public string Dataset { get; set; } = "";
    public string Scope { get; set; } = "";
    public string Key { get; set; } = "";
    public string Data { get; set; } = "";
    public string? SourceId { get; set; }
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
}

public sealed class PoliceDbContext(DbContextOptions<PoliceDbContext> options) : DbContext(options)
{
    public DbSet<PoliceRecord> Records => Set<PoliceRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var record = model.Entity<PoliceRecord>();
        record.ToTable("PoliceRecords", table =>
        {
            table.HasCheckConstraint("CK_PoliceRecords_Dataset", "\"Dataset\" IN ('forces','crimes','stop-searches')");
            table.HasCheckConstraint("CK_PoliceRecords_Times", "\"UpdatedAtUtc\" >= \"FirstSeenUtc\" AND \"LastSeenUtc\" >= \"UpdatedAtUtc\"");
        });
        record.HasKey(x => new { x.Dataset, x.Scope, x.Key });
        record.Property(x => x.Dataset).HasMaxLength(20);
        record.Property(x => x.Scope).HasMaxLength(80);
        record.Property(x => x.Key).HasMaxLength(160);
        record.Property(x => x.Data).IsRequired().HasColumnType("text");
        record.Property(x => x.SourceId).HasMaxLength(160);
        record.HasIndex(x => new { x.Dataset, x.Scope, x.SourceId }).IsUnique();
    }
}

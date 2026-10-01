using Microsoft.EntityFrameworkCore;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>
/// The schema, in one place (Spec 008 §4). Migrations are generated from it; <c>EnsureCreated</c> is never used.
/// Names are spelled out, snake_case, so the SQL in this folder and in the tests reads like the database.
/// </summary>
internal sealed class CooDbContext(DbContextOptions<CooDbContext> options) : DbContext(options)
{
    public DbSet<KindVersionRecord> KindVersions => Set<KindVersionRecord>();

    public DbSet<HouseholdRecord> Households => Set<HouseholdRecord>();

    public DbSet<CareEntryRecord> CareEntries => Set<CareEntryRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KindVersionRecord>(e =>
        {
            e.ToTable("entry_kind_versions", t => t.HasCheckConstraint("ck_entry_kind_versions_version", "version >= 1"));
            e.HasKey(k => new { k.Kind, k.Version });
            e.Property(k => k.Kind).HasColumnName("kind");
            e.Property(k => k.Version).HasColumnName("version");
        });

        modelBuilder.Entity<HouseholdRecord>(e =>
        {
            e.ToTable("households", t => t.HasCheckConstraint("ck_households_next_seq", "next_seq >= 1"));
            e.HasKey(h => h.Id);
            e.Property(h => h.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(h => h.NextSeq).HasColumnName("next_seq").HasDefaultValue(1L);
            e.Property(h => h.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CareEntryRecord>(e =>
        {
            e.ToTable("care_entries", t =>
            {
                // G3: the database refuses what the code does not know (ADR 0010).
                t.HasCheckConstraint("ck_care_entries_details_object", "jsonb_typeof(details) = 'object'");
                t.HasCheckConstraint("ck_care_entries_one_subject", "(subject_baby_id IS NULL) <> (subject_person IS NULL)");
                t.HasCheckConstraint("ck_care_entries_end_pair", "(end_at IS NULL) = (end_offset_min IS NULL)");
                t.HasCheckConstraint("ck_care_entries_point_has_no_end", "is_session OR end_at IS NULL");
                t.HasCheckConstraint("ck_care_entries_offsets", "start_offset_min BETWEEN -840 AND 840 AND (end_offset_min IS NULL OR end_offset_min BETWEEN -840 AND 840)");
                t.HasCheckConstraint("ck_care_entries_seq", "seq >= 1");
            });
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(c => c.HouseholdId).HasColumnName("household_id");
            e.Property(c => c.Kind).HasColumnName("kind");
            e.Property(c => c.SubjectBabyId).HasColumnName("subject_baby_id");
            e.Property(c => c.SubjectPerson).HasColumnName("subject_person");
            e.Property(c => c.IsSession).HasColumnName("is_session");
            e.Property(c => c.StartAt).HasColumnName("start_at").HasColumnType("timestamptz");
            e.Property(c => c.StartOffsetMin).HasColumnName("start_offset_min");
            e.Property(c => c.EndAt).HasColumnName("end_at").HasColumnType("timestamptz");
            e.Property(c => c.EndOffsetMin).HasColumnName("end_offset_min");
            e.Property(c => c.LoggedBy).HasColumnName("logged_by");
            e.Property(c => c.Source).HasColumnName("source");
            e.Property(c => c.SourcePromptVersion).HasColumnName("source_prompt_version");
            e.Property(c => c.Note).HasColumnName("note");
            e.Property(c => c.Details).HasColumnName("details").HasColumnType("jsonb");
            e.Property(c => c.DetailsVersion).HasColumnName("details_version")
                .HasComputedColumnSql("((details->>'v')::int)", stored: true);
            e.Property(c => c.VersionWallMs).HasColumnName("version_wall_ms");
            e.Property(c => c.VersionCounter).HasColumnName("version_counter");
            e.Property(c => c.VersionDevice).HasColumnName("version_device");
            e.Property(c => c.IsDeleted).HasColumnName("is_deleted");
            e.Property(c => c.Seq).HasColumnName("seq");

            // G3: an unregistered (kind, version) cannot be inserted, whatever code tries.
            e.HasOne<KindVersionRecord>().WithMany()
                .HasForeignKey(c => new { c.Kind, c.DetailsVersion })
                .HasPrincipalKey(k => new { k.Kind, k.Version })
                .HasConstraintName("fk_care_entries_entry_kind_versions")
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<HouseholdRecord>().WithMany()
                .HasForeignKey(c => c.HouseholdId)
                .HasConstraintName("fk_care_entries_households")
                .OnDelete(DeleteBehavior.Restrict);

            // Sync pulls by (household, seq); the timeline and the duplicate check by baby and time (ADR 0010 §2).
            e.HasIndex(c => new { c.HouseholdId, c.Seq }).IsUnique().HasDatabaseName("ix_care_entries_household_seq");
            e.HasIndex(c => new { c.HouseholdId, c.SubjectBabyId, c.StartAt }).HasDatabaseName("ix_care_entries_household_baby_start");
        });
    }
}

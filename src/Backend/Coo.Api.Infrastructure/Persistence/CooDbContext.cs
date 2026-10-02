using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>
/// The schema, in one place (Spec 008 §4, Spec 010 §4.2). Migrations are generated from it; <c>EnsureCreated</c> is
/// never used. Identity's tables keep Identity's names (<c>AspNetUsers</c>, …); ours are snake_case, spelled out, so
/// the SQL in this folder and in the tests reads like the database.
/// </summary>
internal sealed class CooDbContext(DbContextOptions<CooDbContext> options) : IdentityUserContext<CooUser, Guid>(options)
{
    public DbSet<KindVersionRecord> KindVersions => Set<KindVersionRecord>();

    public DbSet<HouseholdRecord> Households => Set<HouseholdRecord>();

    public DbSet<CareEntryRecord> CareEntries => Set<CareEntryRecord>();

    public DbSet<AuthNonceRecord> AuthNonces => Set<AuthNonceRecord>();

    public DbSet<DeviceSessionRecord> DeviceSessions => Set<DeviceSessionRecord>();

    public DbSet<MembershipRecord> Memberships => Set<MembershipRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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
            e.Property(h => h.CreatedBy).HasColumnName("created_by");
            e.HasOne<CooUser>().WithMany()
                .HasForeignKey(h => h.CreatedBy)
                .HasConstraintName("fk_households_created_by")
                .OnDelete(DeleteBehavior.SetNull);
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

        modelBuilder.Entity<CooUser>(e => e.Property(u => u.DisplayName).HasMaxLength(200));

        modelBuilder.Entity<AuthNonceRecord>(e =>
        {
            e.ToTable("auth_nonces");
            e.HasKey(n => n.NonceHash);
            e.Property(n => n.NonceHash).HasColumnName("nonce_hash");
            e.Property(n => n.IssuedAt).HasColumnName("issued_at").HasColumnType("timestamptz");
            e.Property(n => n.ConsumedAt).HasColumnName("consumed_at").HasColumnType("timestamptz");
            e.HasIndex(n => n.IssuedAt).HasDatabaseName("ix_auth_nonces_issued_at");
        });

        modelBuilder.Entity<DeviceSessionRecord>(e =>
        {
            e.ToTable("device_sessions", t => t.HasCheckConstraint(
                "ck_device_sessions_hashes_differ", "previous_refresh_hash IS NULL OR previous_refresh_hash <> refresh_hash"));
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(s => s.UserId).HasColumnName("user_id");
            e.Property(s => s.DeviceId).HasColumnName("device_id");
            e.Property(s => s.DeviceName).HasColumnName("device_name").HasMaxLength(60);
            e.Property(s => s.RefreshHash).HasColumnName("refresh_hash");
            e.Property(s => s.PreviousRefreshHash).HasColumnName("previous_refresh_hash");
            e.Property(s => s.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(s => s.LastUsedAt).HasColumnName("last_used_at").HasColumnType("timestamptz");
            e.Property(s => s.RevokedAt).HasColumnName("revoked_at").HasColumnType("timestamptz");
            e.HasOne<CooUser>().WithMany()
                .HasForeignKey(s => s.UserId)
                .HasConstraintName("fk_device_sessions_users")
                .OnDelete(DeleteBehavior.Cascade);

            // One session per phone per user; signing in again on the phone replaces it (Spec 010 §4.2).
            e.HasIndex(s => new { s.UserId, s.DeviceId }).IsUnique().HasDatabaseName("ix_device_sessions_user_device");
            e.HasIndex(s => s.RefreshHash).IsUnique().HasDatabaseName("ix_device_sessions_refresh_hash");
            e.HasIndex(s => s.PreviousRefreshHash).HasDatabaseName("ix_device_sessions_previous_refresh_hash");
        });

        modelBuilder.Entity<MembershipRecord>(e =>
        {
            e.ToTable("memberships", t => t.HasCheckConstraint("ck_memberships_role", "role IN ('owner', 'parent', 'caregiver')"));
            e.HasKey(m => new { m.HouseholdId, m.UserId });
            e.Property(m => m.HouseholdId).HasColumnName("household_id");
            e.Property(m => m.UserId).HasColumnName("user_id");
            e.Property(m => m.Role).HasColumnName("role");
            e.Property(m => m.JoinedAt).HasColumnName("joined_at").HasColumnType("timestamptz");
            e.HasOne<HouseholdRecord>().WithMany()
                .HasForeignKey(m => m.HouseholdId)
                .HasConstraintName("fk_memberships_households")
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CooUser>().WithMany()
                .HasForeignKey(m => m.UserId)
                .HasConstraintName("fk_memberships_users")
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(m => m.UserId).HasDatabaseName("ix_memberships_user");

            // Exactly one Owner per household (ADR 0003 §6).
            e.HasIndex(m => m.HouseholdId).IsUnique().HasFilter("role = 'owner'").HasDatabaseName("ix_memberships_one_owner");
        });
    }
}

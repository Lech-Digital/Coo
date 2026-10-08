with open("src/Backend/Coo.Api.Infrastructure/Persistence/CooDbContext.cs", "r") as f:
    content = f.read()

content = content.replace("public DbSet<InvitationRecord> Invitations => Set<InvitationRecord>();", "public DbSet<InvitationRecord> Invitations => Set<InvitationRecord>();\n\n    public DbSet<SubscriptionRecord> Subscriptions => Set<SubscriptionRecord>();")

content = content.replace("            e.HasIndex(i => i.ExpiresAt).HasDatabaseName(\"ix_invitations_expires_at\");\n        });", """            e.HasIndex(i => i.ExpiresAt).HasDatabaseName("ix_invitations_expires_at");
        });

        modelBuilder.Entity<SubscriptionRecord>(e =>
        {
            e.ToTable("subscriptions");
            e.HasKey(s => s.PurchaseToken);
            e.Property(s => s.PurchaseToken).HasColumnName("purchase_token");
            e.Property(s => s.HouseholdId).HasColumnName("household_id");
            e.Property(s => s.UserId).HasColumnName("user_id");
            e.Property(s => s.ProductId).HasColumnName("product_id");
            e.Property(s => s.Status).HasColumnName("status");
            e.Property(s => s.ExpiryDate).HasColumnName("expiry_date").HasColumnType("timestamptz");
            e.Property(s => s.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(s => s.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.HasOne<HouseholdRecord>().WithMany().HasForeignKey(s => s.HouseholdId)
                .HasConstraintName("fk_subscriptions_households").OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CooUser>().WithMany().HasForeignKey(s => s.UserId)
                .HasConstraintName("fk_subscriptions_users").OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => s.HouseholdId).HasDatabaseName("ix_subscriptions_household_id");
        });""")

with open("src/Backend/Coo.Api.Infrastructure/Persistence/CooDbContext.cs", "w") as f:
    f.write(content)

using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CodingAgentWebUI.Infrastructure.Persistence;

/// <summary>
/// EF Core DbContext for all pipeline persistence.
/// Registered via <c>AddPooledDbContextFactory&lt;PipelineDbContext&gt;</c> — singleton services
/// create short-lived contexts via the factory.
/// </summary>
public class PipelineDbContext : DbContext
{
    public PipelineDbContext(DbContextOptions<PipelineDbContext> options)
        : base(options)
    {
    }

    public DbSet<WorkItemEntity> WorkItems => Set<WorkItemEntity>();
    public DbSet<PipelineRunEntity> PipelineRuns => Set<PipelineRunEntity>();
    public DbSet<ProjectEntity> Projects => Set<ProjectEntity>();
    public DbSet<PipelineJobTemplateEntity> PipelineJobTemplates => Set<PipelineJobTemplateEntity>();
    public DbSet<ProviderConfigEntity> ProviderConfigs => Set<ProviderConfigEntity>();
    public DbSet<AgentProfileEntity> AgentProfiles => Set<AgentProfileEntity>();
    public DbSet<QualityGateConfigEntity> QualityGateConfigs => Set<QualityGateConfigEntity>();
    public DbSet<ReviewerConfigEntity> ReviewerConfigs => Set<ReviewerConfigEntity>();
    public DbSet<ConsolidationRunEntity> ConsolidationRuns => Set<ConsolidationRunEntity>();
    public DbSet<PipelineConfigEntity> PipelineConfig => Set<PipelineConfigEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkItemEntity>(e =>
        {
            e.HasKey(w => w.Id);
            e.Property(w => w.RowVersion).IsRowVersion();
            e.HasIndex(w => w.Status);
            e.HasIndex(w => new { w.IssueIdentifier, w.IssueProviderConfigId, w.Status });
            // Partial unique index: excludes terminal statuses from uniqueness check.
            // Enum ordinals: Succeeded=3, Failed=4, Cancelled=5 (from WorkItemStatus enum definition order)
            // If WorkItemStatus enum is reordered, this filter MUST be updated.
            e.HasIndex(w => new { w.IssueIdentifier, w.IssueProviderConfigId })
                .HasFilter("\"Status\" NOT IN (3, 4, 5)")
                .IsUnique();
            e.Property(w => w.Payload).HasColumnType("jsonb");
            e.Property(w => w.Result).HasColumnType("jsonb");
        });

        modelBuilder.Entity<PipelineRunEntity>(e =>
        {
            e.HasKey(r => r.RunId);
            e.Property(r => r.RowVersion).IsRowVersion();
            e.Property(r => r.SummaryJson).HasColumnType("jsonb");
            e.HasIndex(r => r.StartedAt).IsDescending();
            e.HasIndex(r => r.AgentId);
            e.HasIndex(r => new { r.FinalStep, r.CompletedAt });
        });

        modelBuilder.Entity<ProjectEntity>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.RowVersion).IsRowVersion();
            e.Property(p => p.Settings).HasColumnType("jsonb");
        });

        modelBuilder.Entity<PipelineJobTemplateEntity>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.RowVersion).IsRowVersion();
            e.Property(t => t.Configuration).HasColumnType("jsonb");
        });

        modelBuilder.Entity<ProviderConfigEntity>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.RowVersion).IsRowVersion();
            e.Property(p => p.Configuration).HasColumnType("jsonb");
        });

        modelBuilder.Entity<AgentProfileEntity>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.RowVersion).IsRowVersion();
            e.Property(a => a.Configuration).HasColumnType("jsonb");
        });

        modelBuilder.Entity<QualityGateConfigEntity>(e =>
        {
            e.HasKey(q => q.Id);
            e.Property(q => q.RowVersion).IsRowVersion();
            e.Property(q => q.Configuration).HasColumnType("jsonb");
        });

        modelBuilder.Entity<ReviewerConfigEntity>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.RowVersion).IsRowVersion();
            e.Property(r => r.Configuration).HasColumnType("jsonb");
        });

        modelBuilder.Entity<ConsolidationRunEntity>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.RowVersion).IsRowVersion();
            e.Property(c => c.Data).HasColumnType("jsonb");
        });

        modelBuilder.Entity<PipelineConfigEntity>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.RowVersion).IsRowVersion();
            e.Property(p => p.Configuration).HasColumnType("jsonb");
        });
    }
}

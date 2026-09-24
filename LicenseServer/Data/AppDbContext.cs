using Microsoft.EntityFrameworkCore;

namespace LicenseServer.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<LicenseKey> LicenseKeys => Set<LicenseKey>();
    public DbSet<Activation> Activations => Set<Activation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LicenseKey>()
            .HasIndex(l => l.Code)
            .IsUnique();

        modelBuilder.Entity<Activation>()
            .HasIndex(a => new { a.LicenseKeyId, a.MachineId })
            .IsUnique();

        modelBuilder.Entity<Activation>()
            .HasOne(a => a.LicenseKey)
            .WithMany(l => l.Activations)
            .HasForeignKey(a => a.LicenseKeyId);
    }
}

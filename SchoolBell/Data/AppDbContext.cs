using Microsoft.EntityFrameworkCore;
using SchoolBell.Models;

namespace SchoolBell.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<AudioFile> AudioFiles => Set<AudioFile>();
    public DbSet<Schedule> Schedules => Set<Schedule>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Schedule>()
            .HasOne(s => s.AudioFile)
            .WithMany()
            .HasForeignKey(s => s.AudioFileId);
    }
}


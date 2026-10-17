using AnimatorsWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace AnimatorsWeb.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<AnimJam> AnimJams { get; set; }
        public DbSet<ChannelSettings> ChannelSettings { get; set; }
        public DbSet<ThemePoll> ThemePolls { get; set; }
        public DbSet<WinnerPoll> WinnerPolls { get; set; }
        public DbSet<Winner> Winners { get; set; }
        public DbSet<Participant> Participants { get; set; }
        public DbSet<RulesSettings> RulesSettings { get; set; }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Winner>()
                .HasIndex(w => new { w.AnimJamNumber, w.DiscordUserId, w.Nomination, w.WorkUrl })
                .IsUnique()
                .HasDatabaseName("IX_Winners_AnimJamNumber_DiscordUserId_Nomination_WorkUrl");

            base.OnModelCreating(modelBuilder);
        }
    }
}

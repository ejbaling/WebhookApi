using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RedwoodIloilo.Common.Entities;

namespace WebhookApi.Data
{
    public class AppDbContext : DbContext
    {
        private readonly IConfiguration _configuration;

        public AppDbContext(DbContextOptions<AppDbContext> options, IConfiguration configuration)
            : base(options)
        {
            _configuration = configuration;

            // Explicitly assign DbSet properties so the non-nullable analyzer is satisfied.
            GuestMessages = Set<GuestMessage>();
            GuestResponses = Set<GuestResponse>();
            Configs = Set<Config>();
            Rules = Set<Rule>();
            RuleCategories = Set<RuleCategory>();
        }

        // DbSet properties are non-nullable and assigned in the constructor via Set<T>().
        public DbSet<GuestMessage> GuestMessages { get; set; }
        public DbSet<GuestResponse> GuestResponses { get; set; }
        public DbSet<Config> Configs { get; set; }
        public DbSet<Rule> Rules { get; set; }
        public DbSet<RuleCategory> RuleCategories { get; set; }
    }
}
using Microsoft.EntityFrameworkCore;
using TelegramMedia.Core.Models;

namespace TelegramMedia.Core.Data;

public class AppDbContext : DbContext
{
    public DbSet<MonitoredChat> MonitoredChats => Set<MonitoredChat>();
    public DbSet<DownloadTask> DownloadTasks => Set<DownloadTask>();
    public DbSet<AppSettings> Settings => Set<AppSettings>();

    private readonly string _dbPath;

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public AppDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        if (!options.IsConfigured)
        {
            var path = _dbPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TelegramMediaDownloader", "app.db");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            options.UseSqlite($"Data Source={path}");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MonitoredChat>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.TelegramChatId).IsUnique();
        });

        modelBuilder.Entity<DownloadTask>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => new { t.TelegramChatId, t.MessageId }).IsUnique();
            e.HasIndex(t => t.Status);
        });

        modelBuilder.Entity<AppSettings>(e =>
        {
            e.HasKey(s => s.Id);
        });
    }

    public async Task EnsureCreatedAndSeedAsync()
    {
        await Database.EnsureCreatedAsync();

        // Lightweight schema top-ups for columns added after a DB was first created
        // (EnsureCreated does not alter existing tables).
        await EnsureColumnAsync("MonitoredChats", "IsPaused", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync("DownloadTasks", "Priority", "INTEGER NOT NULL DEFAULT 0");

        if (!await Settings.AnyAsync())
        {
            Settings.Add(new AppSettings());
            await SaveChangesAsync();
        }
        else
        {
            // Upgrade legacy default path template to the new chat-grouped layout.
            // Customized templates are preserved (only the exact old default is touched).
            var existing = await Settings.FirstAsync();
            const string legacyDefault = "{mediaType}/{chatName}";
            const string newDefault = "{chatType}s/{chatName}/{mediaType}";
            if (existing.PathTemplate == legacyDefault)
            {
                existing.PathTemplate = newDefault;
                await SaveChangesAsync();
            }
        }
    }

    /// <summary>Add a column via ALTER TABLE if it doesn't already exist (SQLite).</summary>
    private async Task EnsureColumnAsync(string table, string column, string definition)
    {
        var conn = Database.GetDbConnection();
        await Database.OpenConnectionAsync();
        try
        {
            var exists = false;
            await using (var check = conn.CreateCommand())
            {
                check.CommandText = $"PRAGMA table_info(\"{table}\");";
                await using var reader = await check.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    // PRAGMA table_info columns: cid, name, type, ...
                    if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
            }

            if (!exists)
                await Database.ExecuteSqlRawAsync($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition};");
        }
        finally
        {
            await Database.CloseConnectionAsync();
        }
    }
}

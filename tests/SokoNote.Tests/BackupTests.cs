using System.IO;
using SokoNote.Services;

namespace SokoNote.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sokonote-tests", Guid.NewGuid().ToString("N"));

    public BackupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private void Touch(string name, DateTime writeTime)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTime(path, writeTime);
    }

    [Fact]
    public void Manual_backups_do_not_push_out_daily_ones()
    {
        // regression: daily and manual backups shared one "keep 14 newest files" quota,
        // so pressing "back up now" a few times deleted the automatic daily history
        for (int d = 0; d < 20; d++) Touch($"sokonote-{new DateTime(2026, 10, 1).AddDays(d):yyyyMMdd}.db", DateTime.Now.AddDays(-30));
        for (int m = 0; m < 20; m++) Touch($"sokonote-20261021-1200{m:00}.db", DateTime.Now);

        BackupService.Prune(_dir);

        var files = Directory.GetFiles(_dir).Select(Path.GetFileName).ToList();
        var daily = files.Where(f => f!.Length == "sokonote-20261001.db".Length).OrderBy(f => f).ToList();
        var manual = files.Except(daily).ToList();
        Assert.Equal(BackupService.KeepDays, daily.Count);
        Assert.Equal("sokonote-20261007.db", daily[0]); // the newest 14 by date in the name
        Assert.Equal(BackupService.KeepManual, manual.Count);
        Assert.Contains("sokonote-20261021-120019.db", manual);
    }

    [Fact]
    public void Retention_uses_the_date_in_the_name_not_the_file_time()
    {
        // copying/restoring the folder resets file times; the oldest backup must still go first
        for (int d = 0; d < 15; d++) Touch($"sokonote-{new DateTime(2026, 10, 1).AddDays(d):yyyyMMdd}.db", DateTime.Now.AddMinutes(-d));
        BackupService.Prune(_dir);
        Assert.False(File.Exists(Path.Combine(_dir, "sokonote-20261001.db")));
        Assert.True(File.Exists(Path.Combine(_dir, "sokonote-20261015.db")));
    }

    [Fact]
    public void Damaged_database_is_set_aside_and_latest_backup_restored()
    {
        // a damaged db used to leave the app unable to start even though daily backups sit right next to it
        var db = Path.Combine(_dir, "sokonote.db");
        var backups = Path.Combine(_dir, "backups");
        var good = new Data.WorkLogRepository(Path.Combine(_dir, "seed.db"));
        good.AddTopic("살아남은 주제", "#111111");
        good.BackupTo(Path.Combine(backups, "sokonote-20261020.db"));
        good.BackupTo(Path.Combine(backups, "sokonote-20261021.db"));
        File.WriteAllText(Path.Combine(backups, "sokonote-20261022.db"), "also damaged");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        File.WriteAllText(db, "this is not a sqlite database");
        File.WriteAllText(db + "-wal", "stale wal that must not be applied to the restored copy");
        Assert.ThrowsAny<Exception>(() => new Data.WorkLogRepository(db));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var restored = DatabaseRecovery.RestoreLatestBackup(db, backups);

        Assert.Equal("sokonote-20261021.db", Path.GetFileName(restored)); // newest backup that actually opens
        Assert.False(File.Exists(db + "-wal"));
        Assert.Single(Directory.GetFiles(_dir, "sokonote.db.damaged-*"));    // original kept, not deleted
        Assert.Equal("살아남은 주제", new Data.WorkLogRepository(db).GetTopics().Single().Name);
    }

    [Fact]
    public void Recovery_without_backups_returns_null_and_keeps_the_file()
    {
        var db = Path.Combine(_dir, "sokonote.db");
        File.WriteAllText(db, "damaged");
        Assert.Null(DatabaseRecovery.RestoreLatestBackup(db, Path.Combine(_dir, "backups")));
        Assert.Equal("damaged", File.ReadAllText(db));
    }

    [Fact]
    public void Old_daily_logs_are_pruned()
    {
        // logs were never deleted: one file per day of use, forever
        var today = new DateTime(2026, 10, 22);
        for (int d = 0; d < 40; d++) Touch($"sokonote-{today.AddDays(-d):yyyyMMdd}.log", DateTime.Now);
        Touch("other.log", DateTime.Now.AddYears(-1));

        Logger.PruneOldLogs(_dir, today);

        var left = Directory.GetFiles(_dir).Select(Path.GetFileName).ToList();
        Assert.Equal(Logger.KeepDays + 1, left.Count);                               // + the unrelated file
        Assert.Contains($"sokonote-{today:yyyyMMdd}.log", left);
        Assert.Contains($"sokonote-{today.AddDays(-(Logger.KeepDays - 1)):yyyyMMdd}.log", left);
        Assert.DoesNotContain($"sokonote-{today.AddDays(-Logger.KeepDays):yyyyMMdd}.log", left);
        Assert.Contains("other.log", left);
    }

    [Fact]
    public void Unrelated_files_are_left_alone()
    {
        Touch("notes.txt", DateTime.Now.AddYears(-1));
        Touch("sokonote-old-copy.db", DateTime.Now.AddYears(-1));
        BackupService.Prune(_dir);
        Assert.Equal(2, Directory.GetFiles(_dir).Length);
    }
}

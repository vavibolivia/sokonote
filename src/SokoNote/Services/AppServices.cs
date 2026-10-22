using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SokoNote.Data;

namespace SokoNote.Services;

public static class AppPaths
{
    public static string DataDir { get; private set; } = "";
    public static string DbPath => Path.Combine(DataDir, "sokonote.db");
    public static string BackupDir => Path.Combine(DataDir, "backups");
    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string ClaudeWorkDir => Path.Combine(DataDir, "claude-workspace");

    public static void Init(string? overrideDir)
    {
        DataDir = string.IsNullOrWhiteSpace(overrideDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SokoNote")
            : Path.GetFullPath(overrideDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(BackupDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(ClaudeWorkDir);
    }
}

public static class Logger
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}\n{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            if (string.IsNullOrEmpty(AppPaths.LogDir)) return;
            var file = Path.Combine(AppPaths.LogDir, $"sokonote-{DateTime.Now:yyyyMMdd}.log");
            lock (Gate) File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
        }
        catch
        {
            // logging must never crash the app
        }
    }
}

/// <summary>Encrypts secrets (API key) for the current Windows user with DPAPI.</summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SokoNote.v1");

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public static string Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            return "";
        }
    }
}

public static class BackupService
{
    public const int KeepDays = 14;

    /// <summary>Takes at most one automatic backup per day and prunes old ones.</summary>
    public static void RunDailyBackup(WorkLogRepository repo)
    {
        var target = Path.Combine(AppPaths.BackupDir, $"sokonote-{DateTime.Today:yyyyMMdd}.db");
        if (!File.Exists(target)) repo.BackupTo(target);
        Prune();
    }

    public static string BackupNow(WorkLogRepository repo)
    {
        var target = Path.Combine(AppPaths.BackupDir, $"sokonote-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        repo.BackupTo(target);
        return target;
    }

    public const int KeepManual = 10;

    private static readonly System.Text.RegularExpressions.Regex DailyName = new(@"^sokonote-\d{8}\.db$");
    private static readonly System.Text.RegularExpressions.Regex ManualName = new(@"^sokonote-\d{8}-\d{6}\.db$");

    private static void Prune() => Prune(AppPaths.BackupDir);

    /// <summary>
    /// Keeps the newest <see cref="KeepDays"/> automatic daily backups and, separately, the newest <see cref="KeepManual"/>
    /// manual ones, so "back up now" never pushes out the daily history. Age comes from the timestamp in the file name
    /// (file times change when the folder is copied); files with other names are never touched.
    /// </summary>
    internal static void Prune(string dir)
    {
        var files = new DirectoryInfo(dir).GetFiles("sokonote-*.db");
        var old = files.Where(f => DailyName.IsMatch(f.Name)).OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(KeepDays)
            .Concat(files.Where(f => ManualName.IsMatch(f.Name)).OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(KeepManual));
        foreach (var f in old)
        {
            try { f.Delete(); } catch { /* in use: try again next start */ }
        }
    }
}

public static class DatabaseRecovery
{
    /// <summary>
    /// Replaces a database that cannot be opened with the newest backup that can. The damaged file (and its
    /// -wal/-shm side files, which must not be replayed onto the backup) is renamed, never deleted.
    /// Returns the backup used, or null when no usable backup exists (nothing is changed then).
    /// </summary>
    public static string? RestoreLatestBackup(string dbPath, string backupDir)
    {
        if (!Directory.Exists(backupDir)) return null;
        var candidates = new DirectoryInfo(backupDir).GetFiles("sokonote-*.db")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal).Select(f => f.FullName);
        var usable = candidates.FirstOrDefault(IsHealthy);
        if (usable is null) return null;

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(dbPath + suffix)) File.Move(dbPath + suffix, $"{dbPath}.damaged-{stamp}{suffix}");
        File.Copy(usable, dbPath);
        return usable;
    }

    private static bool IsHealthy(string path)
    {
        try
        {
            var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false,
            }.ToString();
            using var c = new Microsoft.Data.Sqlite.SqliteConnection(cs);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check; SELECT COUNT(*) FROM entries;";
            using var r = cmd.ExecuteReader();
            return r.Read() && r.GetString(0) == "ok";
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// Localization. The default UI language follows the user's region (Windows regional format),
/// e.g. Korea → Korean, everywhere else → English. Users can override it in Settings.
/// </summary>
public static class Loc
{
    public static readonly string[] Supported = { "ko", "en" };

    private static readonly Dictionary<string, string> RegionToLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["KR"] = "ko",
        ["KP"] = "ko",
    };

    public static string Language { get; private set; } = "ko";
    public static CultureInfo Culture { get; private set; } = CultureInfo.CurrentCulture;

    public static string DetectDefault()
    {
        try
        {
            var region = RegionInfo.CurrentRegion.TwoLetterISORegionName;
            if (RegionToLanguage.TryGetValue(region, out var lang)) return lang;
            var ui = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (Supported.Contains(ui)) return ui;
            var fmt = CultureInfo.CurrentCulture.TwoLetterISOLanguageName;
            if (Supported.Contains(fmt)) return fmt;
        }
        catch
        {
            // fall through
        }
        return "en";
    }

    public static string Resolve(string? setting) =>
        string.IsNullOrWhiteSpace(setting) || setting == "auto" || !Supported.Contains(setting) ? DetectDefault() : setting!;

    public static void Apply(string lang)
    {
        Language = Supported.Contains(lang) ? lang : "en";
        Culture = Language == "ko" ? new CultureInfo("ko-KR") : CultureInfo.CurrentCulture.TwoLetterISOLanguageName == "en"
            ? CultureInfo.CurrentCulture
            : new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        Thread.CurrentThread.CurrentCulture = Culture;
        Thread.CurrentThread.CurrentUICulture = Culture;

        var app = System.Windows.Application.Current;
        if (app is null) return;
        var dicts = app.Resources.MergedDictionaries;
        var old = dicts.FirstOrDefault(d => d.Source?.OriginalString.Contains("Strings.", StringComparison.Ordinal) == true);
        var dict = new System.Windows.ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/SokoNote;component/Resources/Strings.{Language}.xaml", UriKind.Absolute),
        };
        if (old is not null) dicts.Remove(old);
        dicts.Add(dict);
    }

    public static string T(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as string ?? key;

    public static string F(string key, params object?[] args)
    {
        try { return string.Format(Culture, T(key), args); }
        catch (FormatException) { return T(key); }
    }

    public static string LongDate(DateTime d) =>
        Language == "ko" ? d.ToString("yyyy년 M월 d일 (ddd)", Culture) : d.ToString("ddd, MMM d, yyyy", Culture);

    public static string ShortDate(DateTime d) =>
        Language == "ko" ? d.ToString("M.d (ddd)", Culture) : d.ToString("ddd, MMM d", Culture);
}

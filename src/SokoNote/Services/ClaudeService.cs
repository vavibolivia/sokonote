using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using SokoNote.Models;

namespace SokoNote.Services;

public enum ClaudeAuthMode
{
    /// <summary>Use the Claude account the Claude Code CLI is signed in with (claude.ai subscription).</summary>
    Subscription,
    /// <summary>Use an Anthropic API key (passed to the CLI as ANTHROPIC_API_KEY).</summary>
    ApiKey,
}

public sealed class ClaudeOptions
{
    public string CliPath { get; set; } = "";
    public ClaudeAuthMode Mode { get; set; } = ClaudeAuthMode.Subscription;
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);
}

public sealed record ClaudeAuthStatus(bool LoggedIn, string? Email, string? Method, string Raw);

public sealed class ClaudeException(string message) : Exception(message);

/// <summary>Talks to Claude through the locally installed Claude Code CLI (<c>claude -p</c>).</summary>
public sealed class ClaudeService
{
    public static string? DetectCliPath(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>
        {
            Path.Combine(home, ".local", "bin", "claude.exe"),
            Path.Combine(localAppData, "Programs", "claude", "claude.exe"),
            Path.Combine(appData, "npm", "claude.cmd"),
        };
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir.Trim(), "claude.exe"));
            candidates.Add(Path.Combine(dir.Trim(), "claude.cmd"));
        }
        return candidates.FirstOrDefault(p =>
        {
            try { return File.Exists(p); }
            catch { return false; }
        });
    }

    /// <summary>
    /// Arguments for a one-shot question. The assistant only needs the journal text we pipe in, and that text can
    /// contain anything pasted from the web (i.e. prompt injection), so it gets no tools at all: no built-in tools
    /// (file reads, web fetch, shell…), no MCP servers from the user's config, and no saved session.
    /// </summary>
    internal static List<string> BuildAskArgs(string? model)
    {
        var args = new List<string>
        {
            "-p", "--output-format", "text",
            "--tools", "",
            "--strict-mcp-config",
            "--no-session-persistence",
        };
        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("--model");
            args.Add(model.Trim());
        }
        return args;
    }

    public async Task<string> AskAsync(string prompt, ClaudeOptions options, CancellationToken ct)
    {
        var args = BuildAskArgs(options.Model);
        var (code, stdout, stderr) = await RunAsync(options, args, prompt, ct).ConfigureAwait(false);
        if (code != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new ClaudeException(detail.Trim().Length == 0 ? $"exit code {code}" : detail.Trim());
        }
        return stdout.Trim();
    }

    public async Task<ClaudeAuthStatus> GetAuthStatusAsync(ClaudeOptions options, CancellationToken ct)
    {
        var (_, stdout, stderr) = await RunAsync(options, new[] { "auth", "status", "--json" }, null, ct).ConfigureAwait(false);
        var raw = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        try
        {
            var start = raw.IndexOf('{');
            if (start >= 0)
            {
                using var json = JsonDocument.Parse(raw[start..]);
                var root = json.RootElement;
                var loggedIn = root.TryGetProperty("loggedIn", out var li) && li.ValueKind == JsonValueKind.True;
                var email = root.TryGetProperty("email", out var em) ? em.GetString() : null;
                var method = root.TryGetProperty("authMethod", out var am) ? am.GetString() : null;
                return new ClaudeAuthStatus(loggedIn, email, method, raw);
            }
        }
        catch (JsonException)
        {
        }
        return new ClaudeAuthStatus(false, null, null, raw);
    }

    /// <summary>Opens a console window running <c>claude auth login</c> so the user can sign in with their Claude account.</summary>
    public static void LaunchLogin(string cliPath, bool logoutFirst)
    {
        var cmd = logoutFirst
            ? $"\"{cliPath}\" auth logout & \"{cliPath}\" auth login"
            : $"\"{cliPath}\" auth login";
        Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"{cmd}\"") { UseShellExecute = true });
    }

    internal static void ConfigureStart(ProcessStartInfo psi, string cliPath, IEnumerable<string> args)
    {
        var ext = Path.GetExtension(cliPath).ToLowerInvariant();
        if (ext is ".cmd" or ".bat")
        {
            // npm installs a claude.cmd shim. cmd.exe has its own quoting: with /s the outer quotes are removed
            // as a pair, so every part can be quoted (paths with spaces, empty values, & in values).
            // Double quotes cannot be escaped for cmd, so they are dropped from values.
            static string Q(string s) => "\"" + s.Replace("\"", "") + "\"";
            psi.FileName = "cmd.exe";
            psi.Arguments = "/d /s /c \"" + string.Join(" ", args.Prepend(cliPath).Select(Q)) + "\"";
        }
        else
        {
            psi.FileName = cliPath;
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
    }

    private static async Task<(int code, string stdout, string stderr)> RunAsync(
        ClaudeOptions options, IEnumerable<string> args, string? stdin, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.CliPath) || !File.Exists(options.CliPath))
            throw new ClaudeException(Loc.T("S.Claude.CliMissing"));

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Directory.Exists(options.WorkingDirectory) ? options.WorkingDirectory : Path.GetTempPath(),
        };
        ConfigureStart(psi, options.CliPath, args);

        if (options.Mode == ClaudeAuthMode.ApiKey && !string.IsNullOrWhiteSpace(options.ApiKey))
            psi.Environment["ANTHROPIC_API_KEY"] = options.ApiKey.Trim();
        else
            psi.Environment.Remove("ANTHROPIC_API_KEY");
        psi.Environment["CLAUDE_CODE_ENTRYPOINT"] = "sokonote";

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            throw new ClaudeException(Loc.F("S.Claude.StartFailed", ex.Message));
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        try
        {
            if (stdin is not null) await proc.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
            proc.StandardInput.Close();
        }
        catch (IOException)
        {
            // process exited early; its stderr explains why
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Timeout);
        try
        {
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (ct.IsCancellationRequested) throw;
            throw new ClaudeException(Loc.T("S.Claude.Timeout"));
        }
        return (proc.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }
}

public enum ClaudeScope
{
    CurrentEntry = 0,
    SelectedDate = 1,
    Last7Days = 2,
    Last30Days = 3,
    CurrentTopic = 4,
    Everything = 5,
}

/// <summary>Turns journal entries + the conversation into a single prompt for <c>claude -p</c>.</summary>
public static class ClaudePromptBuilder
{
    public const int MaxContextChars = 120_000;

    public static string Build(string question, IReadOnlyList<Entry> entries, string scopeLabel,
        IReadOnlyList<ChatMessage> history, DateTime today, string language)
    {
        var sb = new StringBuilder();
        var ko = language == "ko";
        sb.AppendLine(ko
            ? "당신은 '소코 업무정리(SokoNote)' 업무일지 앱에 내장된 업무 비서입니다."
            : "You are the work assistant built into the SokoNote work-journal app.");
        sb.AppendLine(ko
            ? "아래 <journal>의 업무 일지 데이터에 근거해서 답하세요. 데이터에 없는 사실은 추측하지 말고 없다고 말하세요. 답변은 한국어로, 핵심부터 간결하고 구조적으로(제목/글머리표) 작성하세요."
            : "Answer from the journal data in <journal>. Do not invent facts that are not in the data; say so when information is missing. Answer in English, concise and structured (headings/bullets), most important first.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Today: {today:yyyy-MM-dd} ({today.DayOfWeek})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Scope: {scopeLabel} · {entries.Count} entries");
        sb.AppendLine();
        sb.AppendLine("<journal>");
        var used = 0;
        var truncated = 0;
        foreach (var e in entries)
        {
            var block = new StringBuilder();
            block.AppendLine(CultureInfo.InvariantCulture,
                $"### [{e.TopicName}] {e.EntryDate:yyyy-MM-dd} · {(string.IsNullOrWhiteSpace(e.Title) ? "(untitled)" : e.Title)} · status: {e.Status}");
            block.AppendLine(e.PlainText.Trim());
            block.AppendLine();
            if (used + block.Length > MaxContextChars)
            {
                truncated++;
                continue;
            }
            used += block.Length;
            sb.Append(block);
        }
        if (entries.Count == 0) sb.AppendLine(ko ? "(해당 범위에 일지가 없습니다)" : "(no entries in this scope)");
        if (truncated > 0) sb.AppendLine(CultureInfo.InvariantCulture, $"({truncated} older entries omitted for length)");
        sb.AppendLine("</journal>");

        var recent = history.Where(m => !m.IsError).TakeLast(8).ToList();
        if (recent.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("<conversation>");
            foreach (var m in recent)
            {
                var text = m.Content.Length > 4000 ? m.Content[..4000] + "…" : m.Content;
                sb.AppendLine(CultureInfo.InvariantCulture, $"{(m.IsUser ? "User" : "Assistant")}: {text}");
            }
            sb.AppendLine("</conversation>");
        }
        sb.AppendLine();
        sb.AppendLine(ko ? "사용자 질문:" : "User question:");
        sb.AppendLine(question.Trim());
        return sb.ToString();
    }
}

using SokoNote.Models;
using SokoNote.Services;

namespace SokoNote.Tests;

public class PromptBuilderTests
{
    private static Entry E(string title, string text, int day = 1) => new()
    {
        TopicName = "T", Title = title, PlainText = text, EntryDate = new DateTime(2026, 10, day), Status = EntryStatus.Done,
    };

    [Fact]
    public void Contains_journal_question_and_language()
    {
        var p = ClaudePromptBuilder.Build("이번 주 요약?", new[] { E("회의", "배포 확정") }, "최근 7일", Array.Empty<ChatMessage>(),
            new DateTime(2026, 10, 22), "ko");
        Assert.Contains("<journal>", p);
        Assert.Contains("### [T] 2026-10-01 · 회의 · status: Done", p);
        Assert.Contains("배포 확정", p);
        Assert.Contains("한국어", p);
        Assert.EndsWith("이번 주 요약?" + Environment.NewLine, p);
        Assert.DoesNotContain("<conversation>", p);
    }

    [Fact]
    public void Empty_scope_and_untitled_entries()
    {
        var en = ClaudePromptBuilder.Build("q", Array.Empty<Entry>(), "all", Array.Empty<ChatMessage>(), DateTime.Today, "en");
        Assert.Contains("(no entries in this scope)", en);
        var untitled = ClaudePromptBuilder.Build("q", new[] { E("", "x") }, "all", Array.Empty<ChatMessage>(), DateTime.Today, "en");
        Assert.Contains("(untitled)", untitled);
    }

    [Fact]
    public void Context_is_capped()
    {
        var big = Enumerable.Range(0, 100).Select(i => E($"e{i}", new string('x', 5000))).ToList();
        var p = ClaudePromptBuilder.Build("q", big, "all", Array.Empty<ChatMessage>(), DateTime.Today, "en");
        Assert.True(p.Length < ClaudePromptBuilder.MaxContextChars + 5000);
        Assert.Contains("older entries omitted", p);
    }

    [Fact]
    public void History_keeps_last_eight_without_errors()
    {
        var history = Enumerable.Range(0, 12)
            .Select(i => new ChatMessage { Role = i == 11 ? "error" : i % 2 == 0 ? "user" : "assistant", Content = $"msg{i}" })
            .ToList();
        var lines = ClaudePromptBuilder.Build("q", Array.Empty<Entry>(), "all", history, DateTime.Today, "en")
            .Split(Environment.NewLine);
        Assert.DoesNotContain(lines, l => l.EndsWith(": msg11"));
        Assert.DoesNotContain(lines, l => l.EndsWith(": msg2"));
        Assert.Contains(lines, l => l.EndsWith(": msg3"));
        Assert.Contains(lines, l => l.EndsWith(": msg10"));
    }
}

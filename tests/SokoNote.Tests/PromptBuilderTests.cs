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
    public void A_page_is_not_labelled_with_one_date_and_status()
    {
        // regression: a page went to Claude as "### [A] <creation date> · … · status: Note", as if it were one day's note
        var page = new Entry
        {
            IsPage = true, TopicName = "A", Title = "A — 한 장", EntryDate = new DateTime(2026, 1, 3),
            PlainText = "맨 위 메모\n# 2026년\n## 2026-10-22 10월 22일 (목)\n배포",
        };
        var p = ClaudePromptBuilder.Build("q", new[] { page }, "all", Array.Empty<ChatMessage>(), DateTime.Today, "ko");
        Assert.DoesNotContain("2026-01-03", p);
        Assert.DoesNotContain("status: Note", p);
        Assert.Contains("### [A] A — 한 장", p);
        Assert.Contains("## 2026-10-22", p);                                     // per-day dates stay inside
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
    public void A_page_larger_than_the_budget_still_sends_its_newest_part()
    {
        // regression: an entry that did not fit was dropped whole, so a big page never reached Claude at all
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 3000; i++) sb.Append("## 2026-").Append((i % 12 + 1).ToString("00")).Append("-01 day\n").Append(new string('x', 60)).Append(" line ").Append(i).Append('\n');
        var text = sb.ToString() + "가장 최근 메모";
        Assert.True(text.Length > ClaudePromptBuilder.MaxContextChars);
        var page = new Entry { IsPage = true, TopicName = "A", Title = "A — 한 장", PlainText = text };

        var p = ClaudePromptBuilder.Build("q", new[] { page }, "all", Array.Empty<ChatMessage>(), DateTime.Today, "ko");

        Assert.Contains("가장 최근 메모", p);                                   // the newest notes (end of the page) are kept
        Assert.DoesNotContain("line 0\n", p.Replace("\r", ""));                 // the oldest part is what gets cut
        Assert.Contains("older part", p);
        Assert.True(p.Length < ClaudePromptBuilder.MaxContextChars + 5000, $"prompt {p.Length}");
    }

    [Fact]
    public void A_big_page_leaves_room_for_dated_entries()
    {
        var page = new Entry { IsPage = true, TopicName = "A", Title = "A — 한 장", PlainText = new string('p', 300_000) + "\n페이지 끝" };
        var cards = Enumerable.Range(0, 30).Select(i => E($"카드{i}", new string('c', 1000))).ToList();
        var p = ClaudePromptBuilder.Build("q", new[] { page }.Concat(cards).ToList(), "all", Array.Empty<ChatMessage>(), DateTime.Today, "ko");

        Assert.Contains("페이지 끝", p);
        Assert.Contains("카드0", p);                                              // the 40% left over is used for cards
        Assert.True(p.Length < ClaudePromptBuilder.MaxContextChars + 5000, $"prompt {p.Length}");
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

using System.Text.Json;
using SokoNote.Services;

namespace SokoNote.Tests;

public class ClaudeWebTests
{
    [Theory]
    [InlineData("plain")]
    [InlineData("quote \" and 'single' and `backtick` ${x}")]
    [InlineData("line1\nline2\r\n</script><script>alert(1)</script>")]
    [InlineData("한글 업무 일지 tab\tand backslash \\ end")]
    public void Insert_script_embeds_text_as_a_single_safe_string_literal(string text)
    {
        var script = ClaudeWeb.BuildInsertScript(text);
        var line = script.Split('\n').Single(l => l.TrimStart().StartsWith("const t = "));
        var literal = line.Trim()["const t = ".Length..].TrimEnd(';');
        Assert.Equal(text, JsonSerializer.Deserialize<string>(literal));      // round-trips exactly
        Assert.DoesNotContain("</script>", literal, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\n", literal);                                  // raw newlines would break the statement
    }

    [Fact]
    public void Insert_script_never_submits()
    {
        var script = ClaudeWeb.BuildInsertScript("x");
        Assert.DoesNotContain("click(", script);
        Assert.DoesNotContain("submit", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Enter", script);
    }

    [Theory]
    [InlineData("\"ok\"", "ok")]
    [InlineData("\"선택\\n텍스트\"", "선택\n텍스트")]
    [InlineData("null", "")]
    [InlineData(null, "")]
    [InlineData("{not json", "")]
    [InlineData("42", "")]
    public void Script_results_are_decoded(string? json, string expected)
    {
        Assert.Equal(expected, ClaudeWeb.DecodeScriptResult(json));
    }

    [Theory]
    [InlineData("https://claude.ai/new", true)]
    [InlineData("https://claude.ai/chat/0b6c1e", true)]
    [InlineData("https://CLAUDE.AI/new", true)]
    [InlineData("http://claude.ai/new", false)]               // not over https
    [InlineData("https://claude.ai.evil.example/new", false)]
    [InlineData("https://evilclaude.ai/new", false)]
    [InlineData("https://accounts.google.com/signin", false)]
    [InlineData("https://example.com/?ref=claude.ai", false)]
    [InlineData(null, false)]
    public void Journal_is_only_ever_inserted_into_claude_ai(string? url, bool allowed)
    {
        // regression: after following a link inside the embedded browser, "insert with my work" would have pasted
        // the whole journal into whatever site was showing
        Assert.Equal(allowed, ClaudeWeb.IsClaudePage(url is null ? null : new Uri(url)));
    }

    [Fact]
    public void Home_is_claude_ai_over_https()
    {
        Assert.Equal("https", ClaudeWeb.Home.Scheme);
        Assert.Equal("claude.ai", ClaudeWeb.Home.Host);
    }
}

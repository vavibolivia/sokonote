using SokoNote.Services;

namespace SokoNote.Tests;

public class SecurityTests
{
    [Theory]
    [InlineData("https://example.com/a?b=1", LinkOpenAction.Open)]
    [InlineData("http://example.com", LinkOpenAction.Open)]
    [InlineData("mailto:me@example.com", LinkOpenAction.Open)]
    [InlineData(@"file:///C:/docs/report.docx", LinkOpenAction.Open)]
    [InlineData(@"file:///C:/no-such-folder/", LinkOpenAction.Reveal)] // only existing folders open
    [InlineData(@"file:///C:/Users/me/Downloads/setup.exe", LinkOpenAction.Reveal)]
    [InlineData(@"file:///C:/tools/run.BAT", LinkOpenAction.Reveal)]
    [InlineData(@"file:///C:/x/script.ps1", LinkOpenAction.Reveal)]
    [InlineData(@"file:///C:/x/shortcut.lnk", LinkOpenAction.Reveal)]
    [InlineData(@"file://attacker/share/doc.docx", LinkOpenAction.Block)] // UNC: leaks NTLM credentials
    [InlineData(@"\\attacker\share\x.exe", LinkOpenAction.Block)]
    [InlineData("ms-msdt:/id PCWDiagnostic", LinkOpenAction.Block)]
    [InlineData("search-ms:query=x", LinkOpenAction.Block)]
    [InlineData("javascript:alert(1)", LinkOpenAction.Block)]
    [InlineData("ftp://example.com/file", LinkOpenAction.Block)]
    public void Link_click_policy(string url, LinkOpenAction expected)
    {
        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri));
        Assert.Equal(expected, LinkNormalizer.Classify(uri));
    }

    [Theory]
    [InlineData(@"C:\Users\me\Downloads\setup.exe.", LinkOpenAction.Reveal)]     // Windows strips the trailing dot and runs it
    [InlineData(@"C:\Users\me\Downloads\setup.exe . ", LinkOpenAction.Reveal)]
    [InlineData(@"C:\docs\notes.txt:evil.exe", LinkOpenAction.Reveal)]          // alternate data stream
    [InlineData(@"C:\tools\console.msc", LinkOpenAction.Reveal)]                 // not on any blocklist
    [InlineData(@"C:\docs\page.html", LinkOpenAction.Reveal)]                    // local HTML runs script
    [InlineData(@"C:\docs\old.xls", LinkOpenAction.Reveal)]                      // macro-capable legacy Office
    [InlineData(@"C:\docs\noextension", LinkOpenAction.Reveal)]
    [InlineData(@"C:\docs\report.docx", LinkOpenAction.Open)]
    [InlineData(@"C:\docs\plan.HWPX", LinkOpenAction.Open)]
    [InlineData(@"C:\docs\scan.pdf", LinkOpenAction.Open)]
    public void Local_files_open_only_when_they_are_known_documents(string path, LinkOpenAction expected)
    {
        // regression: a blocklist of executable extensions was bypassed by trailing dots, streams and unlisted types
        Assert.Equal(expected, LinkNormalizer.Classify(new Uri(path)));
    }

    [Fact]
    public void Existing_folders_open_in_explorer()
    {
        Assert.Equal(LinkOpenAction.Open, LinkNormalizer.Classify(new Uri(System.IO.Path.GetTempPath())));
    }

    [Fact]
    public void Link_click_policy_null_and_relative()
    {
        Assert.Equal(LinkOpenAction.Block, LinkNormalizer.Classify(null));
        Assert.Equal(LinkOpenAction.Block, LinkNormalizer.Classify(new Uri("docs/a.html", UriKind.Relative)));
    }

    [Fact]
    public void Claude_runs_without_any_tools_or_mcp()
    {
        var args = ClaudeService.BuildAskArgs(null);
        var tools = args.IndexOf("--tools");
        Assert.True(tools >= 0);
        Assert.Equal("", args[tools + 1]);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Contains("--no-session-persistence", args);
        Assert.DoesNotContain("--mcp-config", args);
        Assert.DoesNotContain(args, a => a.Contains("dangerously", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("--model", args);
    }

    [Fact]
    public void Claude_model_is_passed_trimmed()
    {
        var args = ClaudeService.BuildAskArgs("  claude-sonnet-5-5 ");
        Assert.Equal("claude-sonnet-5-5", args[args.IndexOf("--model") + 1]);
    }
}

using System.Diagnostics;
using System.IO;
using SokoNote.Services;

namespace SokoNote.Tests;

public sealed class ClaudeLaunchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sokonote tests " + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Cmd_shim_in_path_with_spaces_receives_every_argument()
    {
        // regression: npm installs claude.cmd; with a space in the path plus the empty "--tools" value,
        // cmd.exe /c stripped the outer quotes and the CLI never started
        Directory.CreateDirectory(_dir);
        var shim = Path.Combine(_dir, "claude.cmd");
        // like npm's shim, forward the arguments with %* (quotes intact)
        File.WriteAllText(shim, "@echo off\r\necho %*\r\n");

        var psi = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        ClaudeService.ConfigureStart(psi, shim, new[] { "-p", "--tools", "", "--model", "a&b" });
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();

        Assert.Equal("\"-p\" \"--tools\" \"\" \"--model\" \"a&b\"", output);
    }

    [Fact]
    public void Exe_is_started_directly()
    {
        var psi = new ProcessStartInfo();
        ClaudeService.ConfigureStart(psi, @"C:\x y\claude.exe", new[] { "-p", "" });
        Assert.Equal(@"C:\x y\claude.exe", psi.FileName);
        Assert.Equal(new[] { "-p", "" }, psi.ArgumentList);
    }
}

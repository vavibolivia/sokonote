using System.Text.Json;

namespace SokoNote.Services;

/// <summary>
/// Helpers for asking Claude through claude.ai in an embedded browser: the user's own claude.ai login,
/// no API key and no Claude Code CLI needed.
/// </summary>
public static class ClaudeWeb
{
    public static readonly Uri Home = new("https://claude.ai/new");

    /// <summary>
    /// Script that puts <paramref name="text"/> into claude.ai's message box the way a Ctrl+V would (long text becomes
    /// a pasted attachment there). It never sends: the user reviews and presses Enter.
    /// Returns "ok", or "no-input" when no message box is on the page (e.g. not signed in yet).
    /// </summary>
    public static string BuildInsertScript(string text)
    {
        var json = JsonSerializer.Serialize(text); // safe JS string literal: quotes, newlines, </script> all escaped
        return $$"""
            (() => {
              const t = {{json}};
              const el = document.querySelector('div.ProseMirror[contenteditable="true"]')
                      || document.querySelector('[contenteditable="true"]')
                      || document.querySelector('textarea');
              if (!el) return 'no-input';
              el.focus();
              if (el.tagName === 'TEXTAREA') {
                el.value = t;
                el.dispatchEvent(new Event('input', { bubbles: true }));
                return 'ok';
              }
              const dt = new DataTransfer();
              dt.setData('text/plain', t);
              const handled = !el.dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true }));
              if (!handled) document.execCommand('insertText', false, t);
              return 'ok';
            })()
            """;
    }

    /// <summary>Journal content may only be put into claude.ai itself (https), never into another site the user navigated to.</summary>
    public static bool IsClaudePage(Uri? uri) =>
        uri is { IsAbsoluteUri: true, Scheme: "https" } && string.Equals(uri.Host, Home.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the text the user selected in the page (e.g. part of an answer).</summary>
    public const string SelectionScript = "window.getSelection ? window.getSelection().toString() : ''";

    /// <summary>ExecuteScriptAsync returns JSON; turn a JSON string result back into text.</summary>
    public static string DecodeScriptResult(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "null") return "";
        try
        {
            return JsonSerializer.Deserialize<string>(json) ?? "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}

# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

SokoNote, branded "Soko Work Diary" (formerly 소코 업무정리), is a Windows WPF (.NET 8) work-journal app. Topics are listed on the left, entries for the selected topic/date sit in the middle, and a Word-like rich-text editor on the right (bold, font, colors, paper color/style, hyperlinks). Data lives in SQLite. A side panel lets the user ask Claude about their journal through the locally installed Claude Code CLI.

The user communicates in Korean, and the UI is bilingual (ko/en).

## Commands

The .NET 8 SDK is installed per-user (not on the default PATH):

```powershell
$env:PATH="$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
dotnet build SokoNote.sln
dotnet test SokoNote.sln
dotnet test tests\SokoNote.Tests --filter "FullyQualifiedName~DocumentTests.Auto_link"   # single test / group
```

Run the app: `src\SokoNote\bin\Debug\net8.0-windows\SokoNote.exe [--data <dir>]`.
`--data` points the app at an alternate data folder (handy for manual testing without touching real data) and also disables the single-instance mutex. Close any running `SokoNote.exe` before rebuilding, or the build fails on locked files.

## Architecture

- **`App.xaml.cs`**: startup order is `AppPaths.Init` → `WorkLogRepository` → language (`Loc`) → daily backup → single-instance check → `MainWindow`. Unhandled dispatcher exceptions trigger an emergency `MainWindow.SaveNow()`.
- **`Data/WorkLogRepository.cs`**: all SQL. Every call opens a short-lived pooled connection (WAL, `foreign_keys=ON`, `busy_timeout`), so it is safe to call from any thread. Topic deletion cascades to entries. Dates are stored as `yyyy-MM-dd`, timestamps as ISO `"o"`. Search uses `LIKE … ESCAPE '\'` via `EscapeLike`. The schema is created inline in `Initialize()` (`SchemaVersion` is stored in `settings`); there is no migration framework yet.
- **Entry content**: rich text is stored as a WPF **XamlPackage** blob (`entries.content`) plus a `plain_text` copy used for search, previews and Claude context. Always update both together; see `DocumentService.Save`/`GetPlainText`. `QueryEntries` returns previews only, unless `IncludeFullText` is set; use `GetEntry` for content.
- **MVVM split**: `MainViewModel` (CommunityToolkit.Mvvm source generators, `[ObservableProperty]`) owns navigation state: topics, filtered entries, selected date/topic/search. The **editor lives in `Views/MainWindow.xaml.cs` code-behind**. It handles the RichTextBox, toolbar sync, autosave (`MarkDirty` → debounce timer → `SaveNow`), links, paste/drop, export and print. The VM raises `BeforeNavigate` (flush the editor) and `OpenEntryRequested` (load another entry) toward the window.
- **Editor pitfall**: toolbar combos are applied only on explicit user actions (`DropDownClosed`, Enter, focus loss), never on `SelectionChanged`. `SyncToolbar` updates the combos from the caret, and applying formatting at that moment wipes WPF's pending (spring-loaded) formatting, such as a just-pressed Ctrl+B.
- **Testable editor logic** lives in `Services/DocumentService.cs` (`AutoLinkBefore`, `FindHyperlink`, serialization, color helpers) and `LinkNormalizer`, not in code-behind. Put new pure text/document logic there so it can be unit tested.
- **Claude integration** (`Services/ClaudeService.cs`, `ViewModels/ClaudeViewModel.cs`): shells out to `claude -p --output-format text` with the prompt on stdin. `Bash,Edit,Write,NotebookEdit` are disallowed, and the working directory is `%APPDATA%\SokoNote\claude-workspace`. Auth mode is either the CLI's logged-in subscription or an API key passed as `ANTHROPIC_API_KEY`. The key is stored DPAPI-encrypted (`SecretProtector`) in `settings`. `ClaudePromptBuilder` packs the entries for the chosen `ClaudeScope` into `<journal>` (capped at `MaxContextChars`) plus the last 8 non-error chat messages. Chat history is persisted in `chat_messages`.
- **`Services/AppServices.cs`**: `AppPaths` (default data dir `%APPDATA%\SokoNote`: `sokonote.db`, `backups/`, `logs/`, `claude-workspace/`), `Logger` (daily file log, never throws), `BackupService` (one SQLite-backup-API copy per day, keeps 14), and `Loc` (localization).
- **Localization**: every UI string is a resource key in `Resources/Strings.ko.xaml` **and** `Strings.en.xaml` (keep both in sync). XAML uses `{DynamicResource S.Key}`; code uses `Loc.T("S.Key")` / `Loc.F(...)`. The language defaults from the Windows region (KR → ko, otherwise en) and can be overridden by the `ui.language` setting. The language is swapped at runtime by replacing the merged dictionary.
- **Styling**: `Themes/Styles.xaml` holds the shared control styles. `Controls/ColorPalette` is the reusable swatch picker used for font, highlight, paper and topic colors.

## Tests

`tests/SokoNote.Tests` (xUnit, references the app project; `InternalsVisibleTo` is set). Repository tests each use a temp SQLite file and call `SqliteConnection.ClearAllPools()` on dispose. Any test that touches WPF text objects (`FlowDocument`, `TextPointer`, …) must run inside `Sta.Run(() => …)`. Without a WPF `Application`, `Loc.T` returns the key itself.

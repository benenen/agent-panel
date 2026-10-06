using System.Diagnostics;
using AgentPanel;
using AgentPanel.Core;
using AgentPanel.Storage;
using AgentPanel.Terminal;
using AgentPanel.Workspace;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

var directory = Path.Combine(Path.GetTempPath(), "agent-panel-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var repository = Path.Combine(directory, "repository");
Directory.CreateDirectory(repository);
try
{
    var database = Path.Combine(directory, "sessions.db");
    var store = new SqliteSessionStore(database);
    var session = AgentSession.Create("Agent ' 中文", repository, "exec /bin/bash -i");
    await store.SaveAsync(session);
    var reopened = new SqliteSessionStore(database);
    Check((await reopened.LoadAsync()).Single() == session, "SQLite reopen and Unicode roundtrip");
    await reopened.SaveAsync(session with { Name = "Updated" });
    Check((await store.LoadAsync()).Single().Name == "Updated", "SQLite upsert");
    await store.DeleteAsync(session.Id);
    Check((await reopened.LoadAsync()).Count == 0, "SQLite delete");

    await Git("init", "-b", "main");
    var workspace = new WorkspaceService();
    Check((await workspace.ReadGitAsync(repository))?.Graph == "尚无提交", "unborn Git branch");
    var filename = "file with spaces 中文.txt";
    await File.WriteAllTextAsync(Path.Combine(repository, filename), "before\n");
    await Git("add", "--", filename);
    await Git("-c", "user.name=Smoke", "-c", "user.email=smoke@example.invalid", "commit", "-m", "initial");
    await File.WriteAllTextAsync(Path.Combine(repository, filename), "staged\n");
    await Git("add", "--", filename);
    await File.WriteAllTextAsync(Path.Combine(repository, filename), "unstaged\n");
    await File.WriteAllTextAsync(Path.Combine(repository, "untracked.txt"), "new\n");
    var git = (await workspace.ReadGitAsync(repository))!;
    Check(git.Changes.Count == 2 && git.Branch == "main" && git.Graph.Contains("initial"), "Git status and graph");
    var diff = await workspace.DiffAsync(git, git.Changes.Single(file => file.Path == filename));
    Check(diff.Contains("+staged") && diff.Contains("+unstaged") && diff.Contains("已暂存"), "staged and unstaged diff");
    Check((await workspace.DiffAsync(git, git.Changes.Single(file => file.Status == "??"))).Contains("new"), "untracked preview");
    await Git("reset", "--hard", "HEAD");
    await Git("mv", "--", filename, "renamed.txt");
    git = (await workspace.ReadGitAsync(repository))!;
    Check(git.Changes.Any(file => file.Path == "renamed.txt" && file.Status.Contains('R')), "NUL-delimited rename parsing");

    if (!args.Contains("--skip-native"))
    {
        using var terminal = new GhosttySession(repository, "exec /bin/bash --noprofile --norc -i");
        terminal.Write("printf '\\033[31mGHOSTTY_OK\\033[0m\\n'\r");
        await WaitFor(() => { terminal.Pump(); return terminal.Snapshot().Cells.Any(cell => cell.GetText() == "G" && cell.Foreground != 0xffe4e4e7); });
        terminal.Resize(100, 30);
        terminal.Write("stty size\r");
        await WaitFor(() => { terminal.Pump(); return Screen(terminal).Contains("30 100"); });
        Check(terminal.Snapshot().Cells.Any(cell => cell.GetText() == "G" && cell.Foreground != 0xffe4e4e7), "Ghostty ANSI color state");
        terminal.Write("printf '\\033[?1049hALT_SCREEN'; sleep 0.3; printf '\\033[?1049l'; printf 'PRIMARY_BACK\\n'\r");
        await WaitFor(() => { terminal.Pump(); var screen = Screen(terminal); return screen.Contains("ALT_SCREEN") && !screen.Contains("GHOSTTY_OK"); });
        await WaitFor(() => { terminal.Pump(); return Screen(terminal).Contains("GHOSTTY_OK") && Screen(terminal).Contains("PRIMARY_BACK"); });
        terminal.Write("exit\r");
        await WaitFor(() => { terminal.Pump(); return terminal.HasExited; });
        Check(terminal.HasExited, "PTY resize, alternate screen, and process exit");
    }
    if (args.Contains("--ui")) RunUiSmoke(repository, !args.Contains("--skip-native"));
    Console.WriteLine("All smoke checks passed.");
}
finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }

async Task Git(params string[] arguments)
{
    var start = new ProcessStartInfo("git") { WorkingDirectory = repository, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync(); await output;
    if (process.ExitCode != 0) throw new Exception(await error);
}
static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    Console.WriteLine("PASS: " + name);
}
static async Task WaitFor(Func<bool> condition)
{
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("PTY smoke check timed out"); await Task.Delay(20); }
}
static string Screen(GhosttySession terminal)
{
    var snapshot = terminal.Snapshot();
    return string.Concat(snapshot.Cells.Take(snapshot.Count).Select(cell => cell.GetText()));
}
static void RunUiSmoke(string directory, bool native)
{
    Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(Path.GetDirectoryName(directory)!, "ui-data"));
    var store = new SqliteSessionStore(SqliteSessionStore.DefaultPath);
    store.SaveAsync(AgentSession.Create("Shell", directory, "exec /bin/bash --noprofile --norc -i")).GetAwaiter().GetResult();
    store.SaveAsync(AgentSession.Create("Agent workspace", directory, "exec /bin/bash --noprofile --norc -i")).GetAwaiter().GetResult();
    AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().WithInterFont().SetupWithoutStarting();
    var window = new MainWindow();
    window.Show();
    for (var i = 0; i < 100; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
    var list = window.FindControl<ListBox>("SessionList")!;
    Check(list.ItemCount == 2, "UI loads SQLite sessions");
    var terminal = window.FindControl<TerminalControl>("Terminal")!;
    Check(terminal is not null, "UI mounts terminal control");
    if (native)
    {
        window.FindControl<Button>("StartSessionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
        window.Activate();
        terminal!.Focus();
        window.KeyTextInput("printf '\\033[32mUI_TERMINAL_OK\\033[0m\\n'");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        for (var i = 0; i < 50; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
        var active = terminal!.Session!;
        // Headless tests have no running dispatcher loop; advance PTY I/O explicitly.
        active.Pump();
        Check(Screen(active).Contains("UI_TERMINAL_OK") && active.Snapshot().Cells.Any(cell => cell.GetText() == "U" && cell.Foreground != 0xffe4e4e7), "UI keyboard reaches real Ghostty PTY");
        list.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Check(terminal.Session is null, "second session has an independent terminal");
        list.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Check(ReferenceEquals(terminal.Session, active), "switching sessions preserves running PTY");
    }
    var search = window.FindControl<TextBox>("SessionSearch")!;
    search.Text = "Shell"; Dispatcher.UIThread.RunJobs();
    Check(list.ItemCount == 1 && ((SessionRow)list.SelectedItem!).Name == "Shell", "session search filters names");
    search.Text = "missing session"; Dispatcher.UIThread.RunJobs();
    Check(list.ItemCount == 0 && terminal!.Session is null && window.FindControl<Border>("TerminalEmpty")!.IsVisible, "empty search clears terminal and shows empty state");
    search.Text = ""; Dispatcher.UIThread.RunJobs();
    Check(list.ItemCount == 2, "clearing search restores sessions");
    var inspector = window.FindControl<Grid>("Inspector")!;
    window.FindControl<Button>("ToggleInspectorButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Check(!inspector.IsVisible, "inspector toggle collapses panel");
    terminal!.Focus();
    window.KeyPressQwerty(PhysicalKey.J, RawInputModifiers.Control | RawInputModifiers.Shift);
    Check(inspector.IsVisible, "inspector shortcut works while terminal is focused");
    window.CaptureRenderedFrame()!.Save("/tmp/agent-panel-empty.png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    list.SelectedIndex = 0;
    for (var i = 0; i < 30; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
    var frame = window.CaptureRenderedFrame();
    Check(frame is not null, "Avalonia headless render");
    frame!.Save("/tmp/agent-panel-preview.png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    window.FindControl<TabControl>("InspectorTabs")!.SelectedIndex = 1;
    for (var i = 0; i < 30; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
    window.CaptureRenderedFrame()!.Save("/tmp/agent-panel-changes.png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    window.FindControl<TabControl>("InspectorTabs")!.SelectedIndex = 2;
    Dispatcher.UIThread.RunJobs();
    window.CaptureRenderedFrame()!.Save("/tmp/agent-panel-graph.png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    window.Close();
}

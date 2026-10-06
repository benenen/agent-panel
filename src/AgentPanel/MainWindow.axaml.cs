using System.Collections.ObjectModel;
using AgentPanel.Core;
using AgentPanel.Storage;
using AgentPanel.Terminal;
using AgentPanel.Workspace;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace AgentPanel;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<AgentSession> _sessions = [];
    private readonly Dictionary<string, GhosttySession> _terminals = [];
    private readonly WorkspaceService _workspace = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private ISessionStore? _store;
    private GitSnapshot? _git;
    private string _directory = Environment.CurrentDirectory;
    private int _refreshVersion, _diffVersion;

    public MainWindow()
    {
        InitializeComponent();
        SessionList.ItemsSource = _sessions;
        _timer.Tick += (_, _) =>
        {
            foreach (var terminal in _terminals.Values)
                if (terminal.Pump() && terminal == Terminal.Session) Terminal.InvalidateVisual();
            if (Terminal.Session is { } active)
                TerminalStatus.Text = active.Error ?? (active.HasExited ? "进程已退出 · 点击「启动 / 重启」重新运行" : "运行中 · Ctrl+Shift+V 粘贴 · 滚轮浏览历史");
        };
        Opened += async (_, _) => await GuardAsync(InitializeAsync);
        Closed += (_, _) => { _timer.Stop(); Terminal.Session = null; foreach (var terminal in _terminals.Values) terminal.Dispose(); };
    }

    private AgentSession? Selected => SessionList.SelectedItem as AgentSession;

    private async Task InitializeAsync()
    {
        _store = new SqliteSessionStore(SqliteSessionStore.DefaultPath);
        foreach (var session in await _store.LoadAsync()) _sessions.Add(session);
        if (_sessions.Count == 0)
        {
            var shell = AgentSession.Create("Shell", _directory, "exec /bin/bash -i");
            await _store.SaveAsync(shell); _sessions.Add(shell);
        }
        SessionList.SelectedIndex = 0;
        _timer.Start();
        StatusText.Text = "会话已从 SQLite 加载 · 点击启动进入终端";
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (DllNotFoundException) { StatusText.Text = "终端依赖未构建，请执行 scripts/build-native.sh 后重新构建应用。"; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException or Microsoft.Data.Sqlite.SqliteException)
        { StatusText.Text = exception.Message; }
    }

    private async void SessionSelected(object? sender, SelectionChangedEventArgs e) => await GuardAsync(async () =>
    {
        _refreshVersion++; _diffVersion++;
        if (Selected is not { } session) { Terminal.Session = null; return; }
        TerminalTitle.Text = session.Name; WorkspaceTitle.Text = session.WorkingDirectory;
        _directory = session.WorkingDirectory;
        Terminal.Session = _terminals.GetValueOrDefault(session.Id);
        await RefreshAsync();
    });

    private async void StartSessionClicked(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (Selected is not { } session || _store is null) return;
        Terminal.Session = null;
        if (_terminals.Remove(session.Id, out var old)) old.Dispose();
        var terminal = new GhosttySession(session.WorkingDirectory, session.Command);
        _terminals.Add(session.Id, terminal); Terminal.Session = terminal; Terminal.Focus();
        await _store.SaveAsync(session with { LastUsed = DateTimeOffset.UtcNow });
        StatusText.Text = $"已启动 {session.Name}";
    });

    private async void NewSessionClicked(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (_store is null) return;
        var session = await new NewSessionWindow(Selected?.WorkingDirectory ?? Environment.CurrentDirectory).ShowDialog<AgentSession?>(this);
        if (session is null) return;
        await _store.SaveAsync(session); _sessions.Add(session); SessionList.SelectedItem = session;
        StatusText.Text = "会话已保存 · 点击启动进入终端";
    });

    private async void DeleteSessionClicked(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (Selected is not { } session || _store is null) return;
        var dialog = new Window { Title = "删除会话", Width = 380, Height = 180, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var delete = new Button { Content = "删除", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        delete.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 20,
            Children =
            { new TextBlock { Text = $"删除「{session.Name}」？运行中的终端也会停止。", TextWrapping = TextWrapping.Wrap }, delete }
        };
        if (!await dialog.ShowDialog<bool>(this)) return;
        await _store.DeleteAsync(session.Id); Terminal.Session = null;
        if (_terminals.Remove(session.Id, out var terminal)) terminal.Dispose();
        _sessions.Remove(session);
        if (_sessions.Count > 0) SessionList.SelectedIndex = 0;
        StatusText.Text = "会话已删除";
    });

    private async void RefreshClicked(object? sender, RoutedEventArgs e) => await GuardAsync(RefreshAsync);
    private async Task RefreshAsync()
    {
        var version = ++_refreshVersion;
        _diffVersion++;
        var directory = _directory;
        FileList.ItemsSource = _workspace.ListFiles(directory); DirectoryLabel.Text = directory;
        var git = await _workspace.ReadGitAsync(Selected?.WorkingDirectory ?? directory);
        if (version != _refreshVersion) return;
        _git = git; ChangeList.ItemsSource = git?.Changes;
        ChangesLabel.Text = git is null ? "当前目录不属于 Git 仓库" : $"{git.Changes.Count} 个修改文件";
        BranchLabel.Text = git is null ? "无 Git 仓库" : $"⑂ {git.Branch}";
        GraphText.Text = git?.Graph ?? "选择 Git 仓库目录后可查看提交历史。";
        DiffLines.Children.Clear();
        if (git?.Changes.Count > 0) ChangeList.SelectedIndex = 0;
        StatusText.Text = "文件与 Git 已刷新";
    }

    private async void FileOpened(object? sender, TappedEventArgs e) => await GuardAsync(async () =>
    {
        if (FileList.SelectedItem is not FileEntry entry) return;
        if (entry.IsDirectory) { _directory = entry.FullPath; await RefreshAsync(); }
        else
        {
            var directory = _directory;
            var text = await _workspace.PreviewAsync(entry.FullPath);
            if (_directory == directory && Equals(FileList.SelectedItem, entry)) FilePreview.Text = text;
        }
    });

    private async void ParentDirectoryClicked(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        _directory = Directory.GetParent(_directory)?.FullName ?? _directory;
        await RefreshAsync();
    });

    private async void ChangeSelected(object? sender, SelectionChangedEventArgs e) => await GuardAsync(async () =>
    {
        var version = ++_diffVersion;
        if (_git is null || ChangeList.SelectedItem is not ChangedFile file) return;
        var diff = await _workspace.DiffAsync(_git, file);
        if (version != _diffVersion) return;
        DiffLines.Children.Clear();
        foreach (var line in diff.Split('\n'))
            DiffLines.Children.Add(new TextBlock
            {
                Text = line,
                FontFamily = new FontFamily("DejaVu Sans Mono"),
                FontSize = 12,
                Foreground = Brush.Parse(line.StartsWith('+') ? "#8EC7AA" : line.StartsWith('-') ? "#E9A1A5" : line.StartsWith("@@", StringComparison.Ordinal) ? "#91B4E9" : "#CDD5E2")
            });
    });
}

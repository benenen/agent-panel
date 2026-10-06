using System.ComponentModel;
using System.Runtime.CompilerServices;
using AgentPanel.Core;
using Avalonia.Media;

namespace AgentPanel;

public sealed class SessionRow(AgentSession session) : INotifyPropertyChanged
{
    private string _state = "未启动";
    public AgentSession Session { get; } = session;
    public string Name => Session.Name;
    public string DirectoryName => Path.GetFileName(Session.WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : Session.WorkingDirectory;
    public string Command => Session.Command;
    public string Avatar => Command.Contains("claude", StringComparison.OrdinalIgnoreCase) ? "✳" : Command.Contains("codex", StringComparison.OrdinalIgnoreCase) ? "C" : ">_";
    public IBrush AvatarBrush => Brush.Parse(Command.Contains("claude", StringComparison.OrdinalIgnoreCase) ? "#C08468" : "#424248");
    public string State => _state;
    public IBrush StateBrush => Brush.Parse(_state == "运行中" ? "#90B89C" : _state == "已退出" ? "#C6A46B" : "#696970");

    public void SetState(string state)
    {
        if (_state == state) return;
        _state = state;
        Changed(nameof(State)); Changed(nameof(StateBrush));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

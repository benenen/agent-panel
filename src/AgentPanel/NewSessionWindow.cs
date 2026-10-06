using AgentPanel.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AgentPanel;

public sealed class NewSessionWindow : Window
{
    public NewSessionWindow(string directory)
    {
        Title = "新建 Agent 会话"; Width = 520; Height = 370; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var name = new TextBox { Text = "Shell", PlaceholderText = "会话名称" };
        var path = new TextBox { Text = directory };
        var command = new TextBox { Text = "exec /bin/bash -i", PlaceholderText = "例如 codex、claude 或 exec /bin/bash -i" };
        var error = new TextBlock { Foreground = Brush.Parse("#E9A1A5"), TextWrapping = TextWrapping.Wrap };
        var create = new Button { Classes = { "primary" }, Content = "创建会话", HorizontalAlignment = HorizontalAlignment.Right };
        create.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(command.Text) || !Directory.Exists(path.Text))
            { error.Text = "请填写名称、启动命令和有效的工作目录。"; return; }
            Close(AgentSession.Create(name.Text, path.Text!, command.Text));
        };
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 10,
            Children =
        {
            new TextBlock { Text = "会话名称" }, name, new TextBlock { Text = "工作目录" }, path,
            new TextBlock { Text = "启动命令" }, command, error, create
        }
        };
    }
}

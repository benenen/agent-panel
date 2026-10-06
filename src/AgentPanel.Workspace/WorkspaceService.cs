using System.Diagnostics;

namespace AgentPanel.Workspace;

public sealed record FileEntry(string Name, string FullPath, bool IsDirectory)
{
    public string Label => (IsDirectory ? "▸  " : "   ") + Name;
}
public sealed record ChangedFile(string Status, string Path)
{
    public string Label => $"{Status}  {Path}";
}
public sealed record GitSnapshot(string Root, string Branch, IReadOnlyList<ChangedFile> Changes, string Graph);

public sealed class WorkspaceService
{
    public IReadOnlyList<FileEntry> ListFiles(string directory) => Directory.EnumerateFileSystemEntries(directory)
        .Select(path => new FileEntry(Path.GetFileName(path), path, Directory.Exists(path)))
        .Where(entry => entry.Name != ".git")
        .OrderByDescending(entry => entry.IsDirectory).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public async Task<string> PreviewAsync(string path)
    {
        if (new FileInfo(path).Length > 512 * 1024) return "文件超过 512 KiB，请使用编辑器打开。";
        var bytes = await File.ReadAllBytesAsync(path);
        return bytes.Contains((byte)0) ? "二进制文件" : System.Text.Encoding.UTF8.GetString(bytes);
    }

    public async Task<GitSnapshot?> ReadGitAsync(string directory)
    {
        var root = await GitAsync(directory, "rev-parse", "--show-toplevel");
        if (root.ExitCode != 0) return null;
        var repository = root.Output.Trim();
        var branch = await GitAsync(repository, "symbolic-ref", "--short", "-q", "HEAD");
        var status = await GitAsync(repository, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        if (status.ExitCode != 0) throw new IOException(status.Error);
        var entries = status.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var changes = new List<ChangedFile>();
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry.Length < 4) continue;
            changes.Add(new(entry[..2], entry[3..]));
            if (entry[..2].Contains('R') || entry[..2].Contains('C')) index++;
        }
        var graph = await GitAsync(repository, "log", "--graph", "--all", "--decorate", "--color=never",
            "--max-count=100", "--format=%h %s%d");
        return new(repository, branch.ExitCode == 0 ? branch.Output.Trim() : "detached HEAD", changes,
            graph.ExitCode == 0 && !string.IsNullOrWhiteSpace(graph.Output) ? graph.Output : "尚无提交");
    }

    public async Task<string> DiffAsync(GitSnapshot repository, ChangedFile file)
    {
        if (file.Status == "??") return "+ 新文件\n" + await PreviewAsync(Path.Combine(repository.Root, file.Path));
        var staged = await GitAsync(repository.Root, "diff", "--cached", "--no-ext-diff", "--no-color", "--", file.Path);
        var unstaged = await GitAsync(repository.Root, "diff", "--no-ext-diff", "--no-color", "--", file.Path);
        if (staged.ExitCode != 0 || unstaged.ExitCode != 0) throw new IOException(staged.Error + unstaged.Error);
        var result = (staged.Output.Length > 0 ? "── 已暂存 ──\n" + staged.Output : "") +
                     (unstaged.Output.Length > 0 ? "── 工作区 ──\n" + unstaged.Output : "");
        return result.Length > 0 ? result : "无文本差异（可能为文件模式修改）";
    }

    private static async Task<(int ExitCode, string Output, string Error)> GitAsync(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 git");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new IOException("Git 操作超时"); }
        return (process.ExitCode, await output, await error);
    }
}

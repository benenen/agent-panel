namespace AgentPanel.Core;

public sealed record AgentSession(string Id, string Name, string WorkingDirectory, string Command, DateTimeOffset LastUsed)
{
    public static AgentSession Create(string name, string directory, string command) =>
        new(Guid.NewGuid().ToString("N"), name.Trim(), Path.GetFullPath(directory), command.Trim(), DateTimeOffset.UtcNow);
}

public interface ISessionStore
{
    Task<IReadOnlyList<AgentSession>> LoadAsync();
    Task SaveAsync(AgentSession session);
    Task DeleteAsync(string id);
}

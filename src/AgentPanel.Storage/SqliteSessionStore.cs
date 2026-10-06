using AgentPanel.Core;
using Microsoft.Data.Sqlite;

namespace AgentPanel.Storage;

public sealed class SqliteSessionStore : ISessionStore
{
    private readonly string _connectionString;

    public SqliteSessionStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, working_directory TEXT NOT NULL,
                command TEXT NOT NULL, last_used TEXT NOT NULL
            );
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") ??
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agent-panel", "sessions.db");

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public async Task<IReadOnlyList<AgentSession>> LoadAsync()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, working_directory, command, last_used FROM sessions ORDER BY last_used DESC";
        using var reader = await command.ExecuteReaderAsync();
        var result = new List<AgentSession>();
        while (await reader.ReadAsync())
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture)));
        return result;
    }

    public async Task SaveAsync(AgentSession session)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions VALUES ($id, $name, $directory, $command, $lastUsed)
            ON CONFLICT(id) DO UPDATE SET name=$name, working_directory=$directory,
                command=$command, last_used=$lastUsed
            """;
        command.Parameters.AddWithValue("$id", session.Id);
        command.Parameters.AddWithValue("$name", session.Name);
        command.Parameters.AddWithValue("$directory", session.WorkingDirectory);
        command.Parameters.AddWithValue("$command", session.Command);
        command.Parameters.AddWithValue("$lastUsed", session.LastUsed.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync();
    }
}

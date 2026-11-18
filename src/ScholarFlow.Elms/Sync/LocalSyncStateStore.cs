using System.Text.Json;
using ScholarFlow.Elms.Models;

namespace ScholarFlow.Elms.Sync;

/// <summary>
/// Persists and loads <see cref="LocalSyncState"/> as JSON inside the Basin's .scholarflow directory.
/// </summary>
public static class LocalSyncStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string GetDefaultStatePath(string outputRootDirectory)
        => Path.Combine(outputRootDirectory, ".scholarflow", "sync_state.json");

    public static async Task<LocalSyncState> LoadAsync(string stateFilePath, CancellationToken ct = default)
    {
        if (!File.Exists(stateFilePath))
            return new LocalSyncState();

        await using var stream = File.OpenRead(stateFilePath);
        return await JsonSerializer.DeserializeAsync<LocalSyncState>(stream, JsonOptions, ct)
               ?? new LocalSyncState();
    }

    public static async Task SaveAsync(string stateFilePath, LocalSyncState state, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(stateFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = $"{stateFilePath}.tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, ct);
        }

        File.Move(tempPath, stateFilePath, overwrite: true);
    }
}

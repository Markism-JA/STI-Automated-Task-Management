using System.Text.Json;
using ScholarFlow.Elms.Models;

namespace ScholarFlow.Elms.Sync;

/// <summary>
/// Persists the course ignore list (.scholarflow/ignore_courses.json).
/// </summary>
public static class CourseFilterStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string GetDefaultFilterPath(string outputRootDirectory)
        => Path.Combine(outputRootDirectory, ".scholarflow", "ignore_courses.json");

    public static async Task<CourseFilter> LoadAsync(string filterFilePath, CancellationToken ct = default)
    {
        if (!File.Exists(filterFilePath))
            return new CourseFilter();

        await using var stream = File.OpenRead(filterFilePath);
        return await JsonSerializer.DeserializeAsync<CourseFilter>(stream, JsonOptions, ct)
               ?? new CourseFilter();
    }

    public static async Task SaveAsync(string filterFilePath, CourseFilter filter, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(filterFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var temp = $"{filterFilePath}.tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, filter, JsonOptions, ct);
        }

        File.Move(temp, filterFilePath, overwrite: true);
    }
}

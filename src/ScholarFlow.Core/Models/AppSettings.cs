namespace ScholarFlow.Core.Models;

public enum ThemeMode
{
    Dark,
    Light
}

public sealed class KnownBasinEntry
{
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTimeOffset LastOpenedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AppSettings
{
    public string? LastOpenedBasinPath { get; set; }
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    public List<KnownBasinEntry> KnownBasins { get; set; } = [];
}

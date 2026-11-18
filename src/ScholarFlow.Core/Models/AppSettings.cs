namespace ScholarFlow.Core.Models;

public sealed class KnownBasinEntry
{
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTimeOffset LastOpenedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AppSettings
{
    public string? LastOpenedBasinPath { get; set; }
    public List<KnownBasinEntry> KnownBasins { get; set; } = [];
}

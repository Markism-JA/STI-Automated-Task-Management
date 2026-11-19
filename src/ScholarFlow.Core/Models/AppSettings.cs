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

    // AI Quiz & Active Recall Provider Configuration
    public string AiProviderPreset { get; set; } = "GeminiAiStudio";
    public string AiBaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
    public string? AiApiKey { get; set; }
    public string AiModelName { get; set; } = "gemini-2.5-flash";

    // Desktop Tray, Notifications, Startup, Onboarding & eLMS Integration
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool EnableDesktopNotifications { get; set; } = true;
    public bool LaunchOnStartup { get; set; }
    public bool HasCompletedOnboarding { get; set; }
    public bool EnableElmsIntegration { get; set; } = true;
}

namespace ScholarFlow.Core.Models;

public enum ThemeMode
{
    Dark,
    Light,
}

public sealed class KnownBasinEntry
{
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTimeOffset LastOpenedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AiProviderProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public bool IsOffline { get; set; }
}

public sealed class AppSettings
{
    public string? LastOpenedBasinPath { get; set; }
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    public List<KnownBasinEntry> KnownBasins { get; set; } = [];

    // AI Quiz & Active Recall Provider Configuration (OpenAI-compatible & Custom Profiles)
    public string AiProviderPreset { get; set; } = "GeminiAiStudio";
    public string AiBaseUrl { get; set; } =
        "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
    public string? AiApiKey { get; set; }
    public string AiModelName { get; set; } = "gemini-2.5-flash";
    public string? ActiveAiProfileId { get; set; }
    public List<AiProviderProfile> SavedAiProfiles { get; set; } = [];

    // Desktop Tray, Notifications, Startup, Onboarding & eLMS Integration
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool EnableDesktopNotifications { get; set; } = true;
    public bool LaunchOnStartup { get; set; }
    public bool HasCompletedOnboarding { get; set; }
    public bool EnableElmsIntegration { get; set; } = true;
}

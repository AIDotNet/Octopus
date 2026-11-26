using Octopus.Core.Settings;

namespace Octopus.App.StarPrompt;

/// <summary>Timing and eligibility thresholds for the bottom-right GitHub Star reminder pill.</summary>
public sealed class StarPromptOptions
{
    /// <summary>False in automated tests so background timers do not fire unexpectedly.</summary>
    public bool AutoStart { get; init; } = true;

    public int MinInstallDays { get; init; } = StarPromptSettings.DefaultMinInstallDays;
    public int MinActiveDays { get; init; } = StarPromptSettings.DefaultMinActiveDays;
    public int MinCompletedTurns { get; init; } = StarPromptSettings.DefaultMinCompletedTurns;
    public int MinSessionStarts { get; init; } = StarPromptSettings.DefaultMinSessionStarts;
    public int FirstSnoozeDays { get; init; } = StarPromptSettings.DefaultFirstSnoozeDays;
    public int SecondSnoozeDays { get; init; } = StarPromptSettings.DefaultSecondSnoozeDays;
    public int MaxPromptCount { get; init; } = StarPromptSettings.DefaultMaxPromptCount;

    /// <summary>Short breathing room after an agent turn finishes so the pill never interrupts reading.</summary>
    public TimeSpan CompletionPauseDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Fallback trigger for terminal-only users: minimum active app duration before checking idle pause.</summary>
    public TimeSpan FallbackActiveDuration { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Fallback trigger for terminal-only users: quiet time without key/pointer input.</summary>
    public TimeSpan FallbackIdlePause { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Polling interval for the fallback idle check.</summary>
    public TimeSpan FallbackPollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the thank-you confirmation pill stays visible after clicking Star.</summary>
    public TimeSpan ThanksDisplayDuration { get; init; } = TimeSpan.FromMilliseconds(2200);

    /// <summary>Do not display over very narrow windows where the pill would crowd workspace content.</summary>
    public double MinWindowWidth { get; init; } = 720;

    public Func<DateTimeOffset> UtcNow { get; init; } = static () => DateTimeOffset.UtcNow;
    public Func<DateOnly> LocalToday { get; init; } = static () => DateOnly.FromDateTime(DateTime.Now);
}

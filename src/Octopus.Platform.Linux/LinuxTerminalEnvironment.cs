using Octopus.Platform.Unix;

namespace Octopus.Platform.Linux;

/// <summary>Fills a missing LANG with <c>C.UTF-8</c>, which glibc and musl both provide.</summary>
public sealed class LinuxTerminalEnvironment(IReadOnlyDictionary<string, string>? snapshot = null, string? appVersion = null)
    : UnixTerminalEnvironmentBase(snapshot, appVersion)
{
    protected override string GetDefaultLang() => "C.UTF-8";
}

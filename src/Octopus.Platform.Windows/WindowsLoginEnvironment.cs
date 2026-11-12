using Octopus.Platform;

namespace Octopus.Platform.Windows;

/// <summary>Windows GUI apps inherit the full user environment, so there is no login shell to ask.</summary>
public sealed class WindowsLoginEnvironment : ILoginEnvironment
{
    public Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(
            HostEnvironmentFilter.SnapshotProcessEnvironment(StringComparer.OrdinalIgnoreCase));
}

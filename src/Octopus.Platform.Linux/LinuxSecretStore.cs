using System.Text;

namespace Octopus.Platform.Linux;

/// <summary>
/// The Secret Service (GNOME Keyring, KWallet) through <c>secret-tool</c> from libsecret. The value goes in on standard
/// input, never on the command line. Without <c>secret-tool</c> on PATH there is no keystore.
/// </summary>
public sealed class LinuxSecretStore(IProcessRunner runner, IExecutableLocator locator) : ISecretStore
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private string? _tool;
    private bool _looked;

    public bool IsAvailable => Tool is not null;

    private string? Tool
    {
        get
        {
            if (_looked) return _tool;
            _tool = locator.FindAll("secret-tool", Environment.GetEnvironmentVariable("PATH")).FirstOrDefault();
            _looked = true;
            return _tool;
        }
    }

    private string Require() => Tool ?? throw new NotSupportedException("secret-tool (libsecret) is not installed.");

    public async Task<string?> GetAsync(string service, string name, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(Require(), ["lookup", "service", service, "account", name], null, null, Timeout, cancellationToken).ConfigureAwait(false);
        // secret-tool exits with 1 and prints nothing when there is no such secret.
        if (result.ExitCode == 1 && result.StandardOutput.Length == 0 && !result.TimedOut) return null;
        if (!result.Succeeded) throw new IOException($"secret-tool lookup failed: {result.StandardError.Trim()}");
        return result.StandardOutput;
    }

    public async Task SetAsync(string service, string name, string value, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunWithInputAsync(Require(), ["store", "--label", $"{service} {name}", "service", service, "account", name],
            Encoding.UTF8.GetBytes(value), Timeout, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded) throw new IOException($"secret-tool store failed: {result.StandardError.Trim()}");
    }

    public async Task DeleteAsync(string service, string name, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(Require(), ["clear", "service", service, "account", name], null, null, Timeout, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded && !(result.ExitCode == 1 && !result.TimedOut)) throw new IOException($"secret-tool clear failed: {result.StandardError.Trim()}");
    }
}

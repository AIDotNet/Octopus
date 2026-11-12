using Octopus.Platform.MacOS;
using Xunit;

namespace Octopus.Platform.Tests;

/// <summary>
/// The real keystore: writes one item, reads it back, replaces it and deletes it. It touches the user's login keychain,
/// so it runs only when <c>OCTOPUS_KEYCHAIN_TESTS=1</c>.
/// </summary>
public class SecretStoreTests
{
    [Fact]
    public async Task The_macOS_keychain_keeps_replaces_and_forgets_a_secret()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("OCTOPUS_KEYCHAIN_TESTS") != "1") return;
        var ct = TestContext.Current.CancellationToken;
        var store = new MacSecretStore();
        var service = "Octopus/test." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Assert.Null(await store.GetAsync(service, "token", ct));
            await store.SetAsync(service, "token", "första ✓", ct);
            Assert.Equal("första ✓", await store.GetAsync(service, "token", ct));
            await store.SetAsync(service, "token", "second", ct);
            Assert.Equal("second", await store.GetAsync(service, "token", ct));
        }
        finally
        {
            await store.DeleteAsync(service, "token", ct);
        }
        Assert.Null(await store.GetAsync(service, "token", ct));
        await store.DeleteAsync(service, "token", ct);
    }

    [Fact]
    public async Task The_memory_store_behaves_like_a_keystore()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new MemorySecretStore();
        Assert.Null(await store.GetAsync("s", "n", ct));
        await store.SetAsync("s", "n", "v", ct);
        Assert.Equal("v", await store.GetAsync("s", "n", ct));
        Assert.Null(await store.GetAsync("other", "n", ct));
        await store.DeleteAsync("s", "n", ct);
        Assert.Null(await store.GetAsync("s", "n", ct));
    }
}

using System.Text.Json;
using Octopus.Core.Agents;
using Octopus.Platform;

namespace Octopus.Storage;

/// <summary>Stores non-secret agent account metadata in <c>&lt;DataDirectory&gt;/accounts.json</c>.</summary>
public sealed class JsonAccountStore(IAppPaths paths) : IAccountStore
{
    private readonly string _path = Path.Combine(paths.DataDirectory, "accounts.json");
    private readonly object _gate = new();
    private bool _loaded;

    public AccountsDocument Current
    {
        get
        {
            EnsureLoaded();
            return field;
        }
        private set;
    } = new();

    public Exception? LastError { get; private set; }

    public event EventHandler? Changed;

    public void Load()
    {
        lock (_gate)
        {
            Current = Read();
            _loaded = true;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Update(Action<AccountsDocument> mutate)
    {
        lock (_gate)
        {
            if (!_loaded)
            {
                Current = Read();
                _loaded = true;
            }
            mutate(Current);
            Current = Normalize(Current);
            try
            {
                var text = JsonSerializer.Serialize(Current, OctopusJsonContext.FileOptions.GetTypeInfo<AccountsDocument>());
                AtomicFile.WriteAllText(_path, text);
                LastError = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                LastError = e;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_gate)
        {
            if (_loaded) return;
            Current = Read();
            _loaded = true;
        }
    }

    private AccountsDocument Read()
    {
        if (!File.Exists(_path) || !AtomicFile.TryReadAllText(_path, out var text)) return new AccountsDocument();
        try
        {
            return Normalize(JsonSerializer.Deserialize(text, OctopusJsonContext.FileOptions.GetTypeInfo<AccountsDocument>()));
        }
        catch (JsonException)
        {
            AtomicFile.Quarantine(_path);
            return new AccountsDocument();
        }
    }

    public static AccountsDocument Normalize(AccountsDocument? document)
    {
        document ??= new AccountsDocument();
        document.Accounts ??= [];
        var seen = new HashSet<(string Agent, string Id)>();
        document.Accounts.RemoveAll(a =>
            a is null
            || string.IsNullOrWhiteSpace(a.AgentId)
            || string.IsNullOrWhiteSpace(a.Id)
            || !seen.Add((a.AgentId.Trim(), a.Id.Trim())));
        foreach (var account in document.Accounts)
        {
            account.AgentId = account.AgentId.Trim();
            account.Id = account.Id.Trim();
            account.Label = string.IsNullOrWhiteSpace(account.Label) ? account.Id : account.Label.Trim();
            if (string.IsNullOrWhiteSpace(account.Directory)) account.Directory = null;
            if (string.IsNullOrWhiteSpace(account.Identity)) account.Identity = null;
            if (string.IsNullOrWhiteSpace(account.PlanTier)) account.PlanTier = null;
        }
        document.ActiveGlobalAccounts ??= [];
        foreach (var key in document.ActiveGlobalAccounts.Keys.Where(k => string.IsNullOrWhiteSpace(k) || string.IsNullOrWhiteSpace(document.ActiveGlobalAccounts[k])).ToList())
        {
            document.ActiveGlobalAccounts.Remove(key);
        }
        return document;
    }
}

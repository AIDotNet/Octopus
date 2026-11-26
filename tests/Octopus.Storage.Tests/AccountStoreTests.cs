using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Xunit;

namespace Octopus.Storage.Tests;

public class AccountStoreTests
{
    [Fact]
    public void RoundTrip_and_json_shape()
    {
        using var paths = new TempPaths();
        var store = new JsonAccountStore(paths);
        var created = new DateTimeOffset(2026, 11, 23, 8, 30, 0, TimeSpan.Zero);

        store.Update(doc =>
        {
            doc.Accounts.Add(new AgentAccountRecord
            {
                Id = "work1234",
                AgentId = "claude",
                Label = "Work Team",
                Origin = AccountOrigin.Managed,
                Directory = "/data/accounts/claude/work1234",
                Identity = "dev@company.com",
                PlanTier = "Max 20x",
                CreatedAt = created,
            });
            doc.Accounts.Add(new AgentAccountRecord
            {
                Id = "ext56789",
                AgentId = "codex",
                Label = "Personal Plus",
                Origin = AccountOrigin.External,
                Directory = "/Users/me/.codex-personal",
                Identity = "me@example.com",
                PlanTier = "plus",
                CreatedAt = created,
            });
            doc.ActiveGlobalAccounts["agy"] = "agy00001";
        });

        Assert.Null(store.LastError);
        var filePath = Path.Combine(paths.DataDirectory, "accounts.json");
        var text = File.ReadAllText(filePath);
        Assert.Contains("\"origin\": \"managed\"", text);
        Assert.Contains("\"origin\": \"external\"", text);
        Assert.Contains("\"activeGlobalAccounts\"", text);

        var reloaded = new JsonAccountStore(paths);
        reloaded.Load();
        Assert.Equal(2, reloaded.Current.Accounts.Count);
        Assert.Equal("Work Team", reloaded.Current.Accounts[0].Label);
        Assert.Equal(AccountOrigin.Managed, reloaded.Current.Accounts[0].Origin);
        Assert.Equal("dev@company.com", reloaded.Current.Accounts[0].Identity);
        Assert.Equal("Max 20x", reloaded.Current.Accounts[0].PlanTier);
        Assert.Equal(AccountOrigin.External, reloaded.Current.Accounts[1].Origin);
        Assert.Equal("agy00001", reloaded.Current.ActiveGlobalAccounts["agy"]);
    }

    [Fact]
    public void Deduplicates_and_normalizes_accounts_and_quarantines_corrupt_json()
    {
        using var paths = new TempPaths();
        Directory.CreateDirectory(paths.DataDirectory);
        var filePath = Path.Combine(paths.DataDirectory, "accounts.json");

        File.WriteAllText(filePath, """
            {
              "accounts": [
                { "id": "a1", "agentId": "claude", "label": "  First  ", "origin": "managed", "directory": "   " },
                { "id": "a1", "agentId": "claude", "label": "Duplicate", "origin": "managed" },
                { "id": "", "agentId": "claude", "label": "MissingId" }
              ],
              "activeGlobalAccounts": { "agy": "g1", "empty": "  " }
            }
            """);

        var store = new JsonAccountStore(paths);
        store.Load();
        var single = Assert.Single(store.Current.Accounts);
        Assert.Equal("First", single.Label);
        Assert.Null(single.Directory);
        Assert.Equal("g1", Assert.Single(store.Current.ActiveGlobalAccounts).Value);

        File.WriteAllText(filePath, "{ broken json");
        var second = new JsonAccountStore(paths);
        second.Load();
        Assert.Empty(second.Current.Accounts);
        Assert.Single(Directory.GetFiles(paths.DataDirectory, "accounts.json.corrupt-*"));
    }

    [Fact]
    public async Task Project_agent_accounts_and_launch_account_id_round_trip()
    {
        using var paths = new TempPaths();
        var doc = new ProjectsDocument
        {
            Projects =
            [
                new Project
                {
                    Id = "p1",
                    Name = "Repo",
                    Path = "/tmp/repo",
                    AgentAccounts = new Dictionary<string, string>
                    {
                        ["claude"] = "work1234",
                        ["codex"] = "  ",
                        [""] = "ignored",
                    },
                    Items =
                    [
                        new ProjectItem
                        {
                            Id = "i1",
                            Kind = ProjectItemKind.Agent,
                            AgentId = "claude",
                            Title = "Claude",
                            Launch = new AgentLaunchOptions { AccountId = "personal99", Model = "opus" },
                        },
                    ],
                },
            ],
        };

        await new JsonProjectStore(paths).SaveAsync(doc, TestContext.Current.CancellationToken);
        var loaded = await new JsonProjectStore(paths).LoadAsync(TestContext.Current.CancellationToken);
        var project = Assert.Single(loaded.Projects);
        Assert.Equal("work1234", Assert.Single(project.AgentAccounts).Value);
        Assert.Equal("personal99", project.Items[0].Launch!.AccountId);

        var profile = new AgentProfile { DefaultAccountId = "work1234" };
        using var settings = new JsonSettingsStore(paths);
        settings.Update(s => s.Agents.Profiles["claude"] = profile);
        settings.Flush();

        var reloadedSettings = new JsonSettingsStore(paths);
        reloadedSettings.Load();
        Assert.Equal("work1234", reloadedSettings.Current.Agents.Profiles["claude"].DefaultAccountId);
    }
}

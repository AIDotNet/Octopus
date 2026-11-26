using Avalonia.Headless.XUnit;
using Octopus.Mobile.Common;
using Octopus.Mobile.Hosts;
using Octopus.Mobile.Workspace;
using Octopus.Remote.Protocol;
using Octopus.Remote.Tests;
using Xunit;

namespace Octopus.Mobile.Tests;

/// <summary>Where a session's work came from when an agent (often on another computer) delegated it.</summary>
public class CollabOriginTests
{
    private static RemoteProject Project()
    {
        var host = new RemoteHost("h1", "工作站", HostLink.Lan, isPaired: true);
        return new RemoteProject(host, "p1", "Octopus", "~/code/Octopus");
    }

    [AvaloniaFact]
    public void A_session_delegated_from_another_computer_names_that_computer_and_session()
    {
        var project = Project();
        var session = new RemoteSession(project, "s1", SessionKind.Agent, "跑一遍集成测试") { IsGui = true, OriginDevice = "MacBook Pro", OriginSession = "Claude Code · Lead" };
        project.Sessions.Add(session);
        using var row = new SessionRowViewModel(session, _ => { });

        Assert.True(row.HasOrigin);
        Assert.Equal("来自 MacBook Pro · Claude Code · Lead", row.OriginText);

        // A session its own host's agent started names that session instead.
        var lead = new RemoteSession(project, "s0", SessionKind.Agent, "主控");
        project.Sessions.Add(lead);
        var child = new RemoteSession(project, "s2", SessionKind.Agent, "写测试") { ParentItemId = "s0" };
        project.Sessions.Add(child);
        using var childRow = new SessionRowViewModel(child, _ => { });
        Assert.Equal("由 主控 委托", childRow.OriginText);

        // A session a person started says nothing.
        using var plain = new SessionRowViewModel(lead, _ => { });
        Assert.False(plain.HasOrigin);
    }

    [AvaloniaFact]
    public void A_collaboration_message_in_the_timeline_is_a_card_not_the_persons_bubble()
    {
        var entry = new ConversationUserEntry("u1")
        {
            Text = "[Octopus Collab Task #task_3_ab12 · Thread #t_4_cd34 · Delegated by @Claude Code · Lead · From device MacBook Pro]\n"
                + "跑一遍集成测试并汇报失败用例。\n\n"
                + "When finished (or if you need clarification from the delegating agent), conclude with a concise summary or your question.",
        };

        Assert.True(entry.IsCollabMessage);
        Assert.False(entry.IsPersonMessage);
        Assert.Equal("Collab Task · Delegated by @Claude Code · Lead", entry.CollabHeader);
        Assert.Equal("来自 MacBook Pro", entry.CollabFromDeviceText);
        Assert.Equal("跑一遍集成测试并汇报失败用例。", entry.DisplayText);

        entry.Text = "帮我看看这个报错";
        Assert.False(entry.IsCollabMessage);
        Assert.True(entry.IsPersonMessage);
        Assert.Equal("帮我看看这个报错", entry.DisplayText);
    }

    [AvaloniaFact]
    public async Task The_phone_learns_where_a_hosts_session_came_from_over_the_protocol()
    {
        await using var relay = new FakeRelay();
        await using var studio = await Computer.StartAsync(relay);
        var octopus = studio.Workspace.AddProject("Octopus", "~/code/Octopus");
        var delegated = studio.Workspace.AddItem(octopus, "跑一遍集成测试", "claude", ItemActivities.Working, surface: ItemSurfaces.Gui);
        delegated.OriginDevice = "MacBook Pro";
        delegated.OriginSession = "Claude Code · Lead";
        using var phone = new RealPhone(relay);
        phone.SignIn(relay);
        phone.Pair(studio);
        var hosts = phone.Model<HostsViewModel>();
        hosts.Online.Single().OpenCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is HostPage));
        var host = phone.Model<HostViewModel>();
        Assert.True(Phone.Until(() => host.Sections.Count == 1 && host.Sections[0].Sessions.Count == 1), "the session is listed");
        Assert.Equal("来自 MacBook Pro · Claude Code · Lead", host.Sections[0].Sessions[0].OriginText);

        // The host says it changed (a later item/changed carries the same fields): kept up to date.
        delegated.OriginSession = "Claude Code · 登录重构";
        studio.Workspace.SetActivity(delegated, ItemActivities.Idle);
        Assert.True(Phone.Until(() => host.Sections[0].Sessions[0].OriginText == "来自 MacBook Pro · Claude Code · 登录重构"), "the origin follows the host");
    }
}

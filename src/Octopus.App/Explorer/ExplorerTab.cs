namespace Octopus.App.Explorer;

public enum ExplorerTab
{
    Files,
    Git,
    /// <summary>The port forwards of a project on an SSH host (design/11-SSH隧道.md).</summary>
    Tunnels,
    /// <summary>The agents' saved sessions of the project's folder (design/ui/会话管理方案.html).</summary>
    Sessions
}

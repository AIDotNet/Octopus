using System.Net;
using System.Net.Sockets;

namespace Octopus.App.Services;

/// <summary>The ports of this computer, as far as the tunnels need to know.</summary>
public interface ILocalPorts
{
    /// <summary>
    /// True when the port can be bound on the loopback address, which is to say nothing listens on it. Found out by
    /// binding it: asking the system for the list of listening ports is no use, on macOS it only lists this process's own.
    /// </summary>
    bool IsFree(int port);

    /// <summary>A port that is free now (the system picks it).</summary>
    int FindFree();
}

public sealed class LocalPorts : ILocalPorts
{
    public bool IsFree(int port)
    {
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public int FindFree()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }
}

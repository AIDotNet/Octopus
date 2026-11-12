using Octopus.SignalForwarder;

// Never lets a failure reach the agent: a missing Octopus, a dead socket or a bad argument must not break the
// agent's status line or make its hook report an error.
try
{
    return await Forwarder.RunAsync(
        args,
        SignalEnvironment.FromProcess(),
        Console.OpenStandardInput(),
        Console.OpenStandardOutput());
}
catch
{
    return 0;
}

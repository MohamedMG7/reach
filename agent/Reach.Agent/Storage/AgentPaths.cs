namespace Reach.Agent.Storage;

/// <summary>Where the agent keeps its files. Tests point <see cref="Root"/> at a temp directory.</summary>
public sealed class AgentPaths(string root)
{
    public static AgentPaths Default { get; } =
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Reach"));

    public string Root { get; } = root;
    public string IdentityKey => Path.Combine(Root, "identity.key");
    public string Devices => Path.Combine(Root, "devices.json");
    public string Commands => Path.Combine(Root, "commands.json");
    public string Logs => Path.Combine(Root, "logs");
}

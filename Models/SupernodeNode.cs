namespace MikuN2N.Models;

/// <summary>
/// One self-hosted supernode the client can connect through. Replaces the single
/// hardcoded server/community pair so a fork ships no service address of its own.
/// </summary>
public sealed class SupernodeNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public string Community { get; set; } = string.Empty;
}

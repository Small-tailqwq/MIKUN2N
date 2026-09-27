namespace MikuN2N.Models;

public enum ThemePreference
{
    System,
    Light,
    Dark
}

public enum ClosePreference
{
    Ask,
    MinimizeToTray,
    Exit
}

public enum DiagnosticUploadPreference
{
    Ask,
    AlwaysAllow,
    AlwaysDeny
}

public sealed class AppSettings
{
    /// <summary>
    /// Supernodes the user added. Ships empty on purpose: the client has no built-in
    /// server address, so a fork or a release never publishes someone's private node.
    /// </summary>
    public List<SupernodeNode> Nodes { get; set; } = [];

    public string ActiveNodeId { get; set; } = string.Empty;

    public string Nickname { get; set; } = Environment.MachineName;
    public string NodeId { get; set; } = Guid.NewGuid().ToString("N");
    public bool RememberKey { get; set; }
    public string? ProtectedKey { get; set; }
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public ClosePreference CloseBehavior { get; set; } = ClosePreference.Ask;
    public int LogRetentionDays { get; set; } = 30;
    public bool ExperimentalIpv6P2p { get; set; }
    public DiagnosticUploadPreference DiagnosticUpload { get; set; } = DiagnosticUploadPreference.Ask;
    public string DiagnosticUploadTarget { get; set; } = string.Empty;
    public bool AutoCheckUpdates { get; set; } = true;
    public string SkippedUpdateVersion { get; set; } = string.Empty;

    /// <summary>Pre-0.6 single supernode. Written only when it still holds a value, and cleared once read.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("Server")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyServer { get; set; }

    /// <summary>Pre-0.6 single community. Written only when it still holds a value, and cleared once read.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("Community")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyCommunity { get; set; }

    /// <summary>
    /// The node the client connects through. Consumes a single node only when nothing
    /// is selected yet; an id pointing at a deleted node returns null so the caller can
    /// ask again instead of silently connecting somewhere else.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SupernodeNode? ActiveNode =>
        !string.IsNullOrEmpty(ActiveNodeId)
            ? Nodes.FirstOrDefault(node => node.Id == ActiveNodeId)
            : Nodes.FirstOrDefault();
}

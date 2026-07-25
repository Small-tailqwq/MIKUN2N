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

public sealed class AppSettings
{
    public string Server { get; set; } = "vps.example.com:3076";
    public string Community { get; set; } = "mygroup";
    public string Nickname { get; set; } = Environment.MachineName;
    public string NodeId { get; set; } = Guid.NewGuid().ToString("N");
    public bool RememberKey { get; set; }
    public string? ProtectedKey { get; set; }
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public ClosePreference CloseBehavior { get; set; } = ClosePreference.Ask;
    public int LogRetentionDays { get; set; } = 30;
}

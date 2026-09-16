using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using MikuN2N.Models;

namespace MikuN2N.Services;

public sealed class SettingsStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MikuN2N.Settings.v1");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    private readonly string _settingsPath;

    public SettingsStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MikuN2N");
        if (TestBuildProfile.Current is { } profile)
            directory = Path.Combine(directory, "tests", profile.BatchId);
        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, "settings.json");
    }

    /// <summary>
    /// Reads the settings file, bringing a file written by an older build up to the node
    /// model. <paramref name="upgraded"/> reports whether that conversion happened, so the
    /// caller can write the result back instead of leaving the old address on disk.
    /// </summary>
    public AppSettings Load(out bool upgraded)
    {
        upgraded = false;
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return TestBuildProfile.Current?.CreateSettings() ?? new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), JsonOptions)
                           ?? new AppSettings();
            var upgradedSettings = false;
            var normalized = Normalize(settings, ref upgradedSettings);
            upgraded = upgradedSettings;
            return normalized;
        }
        catch
        {
            upgraded = false;
            return TestBuildProfile.Current?.CreateSettings() ?? new AppSettings();
        }
    }

    public AppSettings Load() => Load(out _);

    /// <summary>
    /// Brings a settings file written by an older build up to the node-list model. The
    /// pre-0.6 fields held one server and one community, so they become the first node;
    /// both are then cleared so a later save drops the address from disk.
    /// </summary>
    private static AppSettings Normalize(AppSettings settings, ref bool upgraded)
    {
        var servers = EdgeController.SplitServers(settings.LegacyServer ?? string.Empty);
        upgraded = settings.LegacyServer is not null || settings.LegacyCommunity is not null;
        if (servers.Count > 0 && settings.Nodes.Count == 0)
        {
            settings.Nodes.Add(new SupernodeNode
            {
                Name = "默认节点",
                Server = string.Join(", ", servers),
                Community = settings.LegacyCommunity?.Trim() ?? string.Empty
            });
            settings.ActiveNodeId = settings.Nodes[0].Id;
        }

        settings.LegacyServer = null;
        settings.LegacyCommunity = null;
        if (string.IsNullOrEmpty(settings.ActiveNodeId) && settings.Nodes.Count > 0)
        {
            settings.ActiveNodeId = settings.Nodes[0].Id;
        }
        return settings;
    }

    public void Save(AppSettings settings, string key)
    {
        settings.ProtectedKey = settings.RememberKey && !string.IsNullOrEmpty(key)
            ? Protect(key)
            : null;

        var temporaryPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions), new UTF8Encoding(false));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public string LoadKey(AppSettings settings)
    {
        if (!settings.RememberKey || string.IsNullOrWhiteSpace(settings.ProtectedKey))
        {
            return string.Empty;
        }

        try
        {
            var cipher = Convert.FromBase64String(settings.ProtectedKey);
            var clear = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Protect(string value)
    {
        var clear = Encoding.UTF8.GetBytes(value);
        var cipher = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(clear);
        return Convert.ToBase64String(cipher);
    }
}

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
        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), JsonOptions)
                   ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings, string key)
    {
        settings.ProtectedKey = settings.RememberKey && !string.IsNullOrEmpty(key)
            ? Protect(key)
            : null;

        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, JsonOptions), new UTF8Encoding(false));
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

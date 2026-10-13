using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;
using MikuN2N.Models;
using Application = System.Windows.Application;

namespace MikuN2N.Services;

public sealed class ThemeManager : IDisposable
{
    private readonly DispatcherTimer _systemThemeTimer;
    private ThemePreference _preference;
    private bool? _lastSystemLight;
    private bool _isLight = true;

    public ThemeManager()
    {
        _systemThemeTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background,
            (_, _) => RefreshSystemTheme(), Dispatcher.CurrentDispatcher);
    }

    public ThemePreference Preference => _preference;

    public void Apply(ThemePreference preference)
    {
        _preference = preference;
        if (preference == ThemePreference.System)
        {
            _systemThemeTimer.Start();
            RefreshSystemTheme(force: true);
            return;
        }

        _systemThemeTimer.Stop();
        ApplyPalette(preference == ThemePreference.Light);
    }

    public void Dispose() => _systemThemeTimer.Stop();

    public void ApplyWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }
        var dark = _isLight ? 0 : 1;
        if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        }
    }

    private void RefreshSystemTheme(bool force = false)
    {
        if (_preference != ThemePreference.System)
        {
            return;
        }

        var isLight = IsWindowsLightTheme();
        if (!force && _lastSystemLight == isLight)
        {
            return;
        }
        _lastSystemLight = isLight;
        ApplyPalette(isLight);
    }

    private static bool IsWindowsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch
        {
            return true;
        }
    }

    private static readonly Uri LightPalette = new("pack://application:,,,/Themes/Light.xaml");
    private static readonly Uri DarkPalette = new("pack://application:,,,/Themes/Dark.xaml");

    private void ApplyPalette(bool light)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        var source = light ? LightPalette : DarkPalette;
        var index = -1;
        for (var i = 0; i < merged.Count; i++)
        {
            var current = merged[i].Source?.OriginalString;
            if (current is not null && current.Contains("Themes/", StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }
        if (index < 0 || merged[index].Source != source)
        {
            // Replacing the merged entry in place re-resolves every DynamicResource at once;
            // writing keys into Application.Resources would shadow the palette for good.
            var palette = new ResourceDictionary { Source = source };
            if (index < 0)
            {
                merged.Insert(0, palette);
            }
            else
            {
                merged[index] = palette;
            }
        }
        UpdateWindowChrome(light);
    }

    private void UpdateWindowChrome(bool light)
    {
        _isLight = light;
        if (Application.Current is null)
        {
            return;
        }
        foreach (Window window in Application.Current.Windows)
        {
            ApplyWindow(window);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

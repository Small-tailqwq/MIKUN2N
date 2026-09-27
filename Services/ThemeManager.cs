using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;
using MikuN2N.Models;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

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

    private void ApplyPalette(bool light)
    {
        var colors = light
            ? new Dictionary<string, string>
            {
                ["WindowBackground"] = "#F3F6FA",
                ["Surface"] = "#FFFFFFFF",
                ["SurfaceAlt"] = "#F8FAFD",
                ["SurfaceElevated"] = "#FFFFFFFF",
                ["Border"] = "#DFE5EE",
                ["TextPrimary"] = "#172033",
                ["TextSecondary"] = "#697489",
                ["TextMuted"] = "#929CAF",
                ["Accent"] = "#1CAEA4",
                ["AccentHover"] = "#17968D",
                ["AccentSoft"] = "#DFF6F4",
                ["AccentText"] = "#0B6F68",
                ["ButtonHover"] = "#E9EEF5",
                ["ButtonPressed"] = "#DCE4F0",
                ["RowHover"] = "#F0FAF8",
                ["Success"] = "#2CB47D",
                ["SuccessSoft"] = "#E5F7EF",
                ["SuccessText"] = "#1B7A53",
                ["Warning"] = "#D99728",
                ["WarningSoft"] = "#FFF4D9",
                ["Danger"] = "#E25259",
                ["InputBackground"] = "#FFFFFFFF",
                ["LogBackground"] = "#151B29",
                ["LogText"] = "#D7DEEF",
                ["Overlay"] = "#12000000",
                ["ScrollTrack"] = "#0F697489",
                ["ScrollThumb"] = "#78929CAF"
            }
            : new Dictionary<string, string>
            {
                ["WindowBackground"] = "#10151F",
                ["Surface"] = "#181F2C",
                ["SurfaceAlt"] = "#202837",
                ["SurfaceElevated"] = "#242D3D",
                ["Border"] = "#303B4D",
                ["TextPrimary"] = "#F1F5FB",
                ["TextSecondary"] = "#A7B1C3",
                ["TextMuted"] = "#778399",
                ["Accent"] = "#45D5CB",
                ["AccentHover"] = "#63E2D9",
                ["AccentSoft"] = "#14403D",
                ["AccentText"] = "#45D5CB",
                ["ButtonHover"] = "#28323F",
                ["ButtonPressed"] = "#303B4B",
                ["RowHover"] = "#1B2733",
                ["Success"] = "#45C993",
                ["SuccessSoft"] = "#173A31",
                ["SuccessText"] = "#45C993",
                ["Warning"] = "#F0B34E",
                ["WarningSoft"] = "#3B3020",
                ["Danger"] = "#F06B72",
                ["InputBackground"] = "#141B27",
                ["LogBackground"] = "#0B1018",
                ["LogText"] = "#D7DEEF",
                ["Overlay"] = "#30000000",
                ["ScrollTrack"] = "#26303B4D",
                ["ScrollThumb"] = "#A0778399"
            };

        foreach (var color in colors)
        {
            Application.Current.Resources[color.Key] = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(color.Value));
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

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using MikuN2N.Models;
using MikuN2N.Services;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;

namespace MikuN2N;

public partial class SettingsWindow : Window
{
    private readonly ThemePreference _originalTheme;
    private readonly EasterEggManager _easterEggs;
    private readonly EasterEggVisualController _easterEggVisuals;
    private bool _saved;
    private bool _slotRolling;
    private int _versionClickCount;
    private DateTime _lastVersionClick = DateTime.MinValue;

    public ThemePreference SelectedTheme { get; private set; }
    public ClosePreference SelectedCloseBehavior { get; private set; }
    public int SelectedLogRetentionDays { get; private set; }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Icon = AppIconService.Icon;
        _easterEggs = ((App)Application.Current).EasterEggs;
        _easterEggVisuals = new EasterEggVisualController(this, EasterEggOverlay, _easterEggs);
        SourceInitialized += (_, _) => ((App)Application.Current).ThemeManager.ApplyWindow(this);
        _originalTheme = settings.Theme;
        SelectedTheme = settings.Theme;
        SelectedCloseBehavior = settings.CloseBehavior;
        SelectedLogRetentionDays = settings.LogRetentionDays;
        ThemeSystemButton.IsChecked = settings.Theme == ThemePreference.System;
        ThemeLightButton.IsChecked = settings.Theme == ThemePreference.Light;
        ThemeDarkButton.IsChecked = settings.Theme == ThemePreference.Dark;
        CloseAskButton.IsChecked = settings.CloseBehavior == ClosePreference.Ask;
        CloseTrayButton.IsChecked = settings.CloseBehavior == ClosePreference.MinimizeToTray;
        CloseExitButton.IsChecked = settings.CloseBehavior == ClosePreference.Exit;
        LogRetentionBox.SelectedValue = settings.LogRetentionDays.ToString();
        if (LogRetentionBox.SelectedIndex < 0)
        {
            LogRetentionBox.SelectedValue = "30";
        }
        AboutVersionText.Text = $"版本 {BuildIdentity.Version} · Windows x64";
        Closing += (_, _) =>
        {
            if (!_saved)
            {
                ((App)Application.Current).ThemeManager.Apply(_originalTheme);
            }
        };
    }

    private void ThemeButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }
        ((App)Application.Current).ThemeManager.Apply(CurrentTheme());
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SelectedTheme = CurrentTheme();
        SelectedCloseBehavior = CloseTrayButton.IsChecked == true
            ? ClosePreference.MinimizeToTray
            : CloseExitButton.IsChecked == true
                ? ClosePreference.Exit
                : ClosePreference.Ask;
        SelectedLogRetentionDays = int.TryParse(
            LogRetentionBox.SelectedValue?.ToString(),
            out var retentionDays)
            ? retentionDays
            : 30;
        _saved = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MikuN2N", "logs");
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    private void OpenBundledLicense_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Runtime", "LICENSE-n2n.txt");
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    private void CopyVersion_Click(object sender, RoutedEventArgs e) =>
        Clipboard.SetText($"MikuN2N {BuildIdentity.Version}\nWindows {Environment.OSVersion.Version}\n.NET {Environment.Version}");

    private async void AboutVersionText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var now = DateTime.UtcNow;
        _versionClickCount = now - _lastVersionClick > TimeSpan.FromSeconds(1.25)
            ? 1
            : _versionClickCount + 1;
        _lastVersionClick = now;
        if (_versionClickCount < 7 || _slotRolling)
        {
            return;
        }

        _versionClickCount = 0;
        await RollSlotAsync();
    }

    private async Task RollSlotAsync()
    {
        _slotRolling = true;
        EasterEggResultText.Visibility = Visibility.Collapsed;
        try
        {
            for (var step = 0; step < 30; step++)
            {
                SetSlot(RandomLetter(), Random.Shared.Next(0, 10), RandomLetter());
                await Task.Delay(38 + step * 3);
            }

            string result;
            if (_easterEggs.ShouldAwardJackpot())
            {
                result = Random.Shared.Next(0, 2) == 0 ? "N2N" : "N3N";
                SetSlot(result[0], result[1] - '0', result[2]);
                EasterEggResultText.Text = $"JACKPOT · {result} · 彩虹协议已加载";
                EasterEggResultText.Visibility = Visibility.Visible;
                _easterEggs.ActivateJackpot(result);
            }
            else
            {
                do
                {
                    result = $"{RandomLetter()}{Random.Shared.Next(0, 10)}{RandomLetter()}";
                }
                while (result is "N2N" or "N3N");

                SetSlot(result[0], result[1] - '0', result[2]);
                if (Random.Shared.Next(0, 3) == 0)
                {
                    _easterEggs.AddFly();
                    EasterEggResultText.Text = $"{result} · 蓝色苍蝇 +1（当前 {_easterEggs.FlyCount}）";
                }
                else
                {
                    _easterEggs.AddSpider();
                    EasterEggResultText.Text = $"{result} · 蜘蛛 +1（当前 {_easterEggs.SpiderCount}）";
                }
                EasterEggResultText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _slotRolling = false;
        }
    }

    private void SetSlot(char first, int digit, char last)
    {
        SlotFirstText.Text = first.ToString();
        SlotDigitText.Text = digit.ToString();
        SlotLastText.Text = last.ToString();
    }

    private static char RandomLetter() => (char)('A' + Random.Shared.Next(0, 26));

    private ThemePreference CurrentTheme() => ThemeLightButton.IsChecked == true
        ? ThemePreference.Light
        : ThemeDarkButton.IsChecked == true
            ? ThemePreference.Dark
            : ThemePreference.System;
}

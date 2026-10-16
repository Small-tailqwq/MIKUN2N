using System.Windows;
using MikuN2N.Services;
using Application = System.Windows.Application;

namespace MikuN2N;

public partial class MainWindow
{
    private async Task RequestTrayExitAsync()
    {
        if (_closeInProgress || _trayExitRequested) return;
        _trayExitRequested = true;
        try
        {
            var dialog = OwnedWindows.OfType<Window>()
                .FirstOrDefault(window => window.IsVisible && window is not PeerAssistWindow);
            if (dialog is SettingsWindow settings &&
                !settings.OwnedWindows.OfType<Window>().Any(window => window.IsVisible))
            {
                var settingsCompletion = _settingsCompletion;
                if (!await settings.CloseForExitAsync()) return;
                // ShowDialog must unwind and saved settings must finish applying first.
                if (settingsCompletion is not null) await settingsCompletion;
            }
            else if (dialog is not null)
            {
                FocusOpenDialog();
                return;
            }

            if ((_edgeController.IsRunning || _starting || _connectionBusy) &&
                !ThemedMessageDialog.Confirm(this,
                    "退出后虚拟局域网会断开，正在一起玩的朋友会与你断开连接。",
                    "要退出 MikuN2N 吗？", "退出", "取消")) return;
            await ExitApplicationAsync();
        }
        finally
        {
            _trayExitRequested = false;
        }
    }

    private async Task ExitApplicationAsync(bool installUpdate = false)
    {
        if (_closeInProgress)
        {
            return;
        }
        _closeInProgress = true;
        // Anything thrown from here would escape an async void handler and take the
        // process down before n3n-edge is stopped, leaving it orphaned on the TAP
        // adapter and blocking the next launch.
        try
        {
            // Deliberately do not restore the window here. Exiting from the tray menu
            // must not pull a minimized window back on screen just to close it.
            StatusTitle.Text = "正在断开并退出…";
            IsEnabled = false;
            _logFlushTimer.Stop();
            _uptimeTimer.Stop();
            _updateTimer.Stop();
            await _edgeController.DisposeAsync();
            await FinishDiagnosticsAsync();
        }
        catch (Exception exception)
        {
            CrashLogService.Record("退出流程", exception, fatal: false);
            EdgeController.KillLaunchedEdgeProcesses();
        }
        finally
        {
            _trayIcon.Dispose();
        }

        if (installUpdate)
        {
            InstallUpdateAndRelaunch();
        }

        _allowClose = true;
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Runs with n3n-edge already stopped. A failed swap has been rolled back, so the
    /// relaunch brings back whichever version is on disk either way.
    /// </summary>
    private void InstallUpdateAndRelaunch()
    {
        try
        {
            _updates.InstallStaged();
        }
        catch (Exception exception)
        {
            CrashLogService.Record("安装更新", exception, fatal: false);
            ThemedMessageDialog.Show(null, $"更新没有完成，程序已恢复为原来的版本。\n\n{exception.Message}",
                "更新失败", MessageKind.Warning);
        }
        try
        {
            UpdateService.Relaunch();
        }
        catch (Exception exception)
        {
            CrashLogService.Record("更新后重新启动", exception, fatal: false);
        }
    }
}

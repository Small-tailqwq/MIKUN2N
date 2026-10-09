using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MikuN2N.Models;
using MikuN2N.Services;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;

namespace MikuN2N;

public partial class PeerAssistWindow : Window
{
    private readonly EdgeController _controller;
    private readonly CancellationTokenSource _closed = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly HashSet<string> _busyCircuits = [];
    private PeerSnapshot? _target;
    private bool _refreshing;
    private bool _requesting;
    private int _targetRevision;

    public event EventHandler? RequestsChanged;

    public PeerAssistWindow(EdgeController controller, PeerSnapshot? target)
    {
        InitializeComponent();
        _controller = controller;
        Icon = AppIconService.Icon;
        MainWindow.FitToWorkArea(this);
        SourceInitialized += (_, _) => ((App)Application.Current).ThemeManager.ApplyWindow(this);
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) =>
        {
            _refreshTimer.Start();
            await RefreshAsync();
        };
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _closed.Cancel();
        };
        SetTarget(target);
    }

    public void SetTarget(PeerSnapshot? target)
    {
        if (_requesting)
        {
            return;
        }
        _target = target;
        _targetRevision++;
        TargetText.Text = target is null ? "处理朋友的申请，或结束本次连接中的协助。" : $"通信对象：{target.Nickname}（{target.VirtualIp}）";
        CandidatesPanel.Visibility = target is null ? Visibility.Collapsed : Visibility.Visible;
        CandidatesGrid.ItemsSource = null;
        UpdateRequestButton();
        if (IsLoaded)
        {
            _ = RefreshAsync();
        }
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || _closed.IsCancellationRequested)
        {
            return;
        }
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        try
        {
            RefreshSessionsAndRequests();
            if (_target is { } target)
            {
                var revision = _targetRevision;
                var selected = (CandidatesGrid.SelectedItem as CandidateRow)?.Candidate.Relay.NodeId;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                var candidates = await _controller.GetPeerAssistCandidatesAsync(target.NodeId, timeout.Token);
                if (_closed.IsCancellationRequested || revision != _targetRevision)
                {
                    return;
                }
                var rows = candidates.Select(candidate => new CandidateRow(candidate)).ToArray();
                CandidatesGrid.ItemsSource = rows;
                CandidatesGrid.SelectedItem = rows.FirstOrDefault(row => row.Candidate.Relay.NodeId == selected);
                CandidatesEmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            if (StatusText.Text == "正在读取协助状态…")
            {
                SetStatus(_target is null ? "状态每 5 秒更新，可在下方处理申请或结束协助。" : "状态每 5 秒更新；选择朋友后可发起申请。");
            }
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            SetStatus("读取候选超时，稍后会重试。", true);
        }
        catch (Exception exception)
        {
            SetStatus($"暂时无法刷新：{exception.Message}", true);
        }
        finally
        {
            _refreshing = false;
            RefreshButton.IsEnabled = true;
            UpdateRequestButton();
        }
    }

    private void RefreshSessionsAndRequests()
    {
        var requests = _controller.GetPeerAssistRequests();
        RequestsList.ItemsSource = requests;
        RequestsEmptyText.Visibility = requests.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var sessions = _controller.GetPeerAssistSessions().Select(session => new SessionRow(session)).ToArray();
        SessionsList.ItemsSource = sessions;
        SessionsEmptyText.Visibility = sessions.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RequestsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void Request_Click(object sender, RoutedEventArgs e)
    {
        if (_requesting || _target is not { } target || CandidatesGrid.SelectedItem is not CandidateRow { Candidate.Eligible: true } row)
        {
            return;
        }
        _requesting = true;
        UpdateRequestButton();
        SetStatus($"已向 {row.Name} 发起申请，等待对方同意…");
        try
        {
            await _controller.RequestPeerAssistAsync(target.NodeId, row.Candidate.Relay.NodeId, _closed.Token);
            if (!_closed.IsCancellationRequested)
            {
                SetStatus("对方已同意，正在确认协助线路；以会话中的状态为准。");
            }
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"申请未完成：{exception.Message}", true);
        }
        finally
        {
            _requesting = false;
            UpdateRequestButton();
            if (!_closed.IsCancellationRequested)
            {
                await RefreshAsync();
            }
        }
    }

    private async Task RespondAsync(object sender, bool accept)
    {
        if (sender is not Button { Tag: string circuitId } button || !_busyCircuits.Add(circuitId))
        {
            return;
        }
        button.IsEnabled = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await _controller.RespondPeerAssistAsync(circuitId, accept, timeout.Token);
            SetStatus(accept ? "已同意本次协助，等待线路确认。" : "已拒绝这份申请。");
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"处理申请失败：{exception.Message}", true);
        }
        finally
        {
            _busyCircuits.Remove(circuitId);
            button.IsEnabled = true;
            if (!_closed.IsCancellationRequested)
            {
                await RefreshAsync();
            }
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string circuitId } button || !_busyCircuits.Add(circuitId))
        {
            return;
        }
        button.IsEnabled = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await _controller.StopPeerAssistAsync(circuitId, timeout.Token);
            SetStatus("已结束协助，通信恢复使用原有线路。");
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"暂时无法结束协助：{exception.Message}", true);
        }
        finally
        {
            _busyCircuits.Remove(circuitId);
            button.IsEnabled = true;
            if (!_closed.IsCancellationRequested)
            {
                await RefreshAsync();
            }
        }
    }

    private void SetStatus(string message, bool error = false)
    {
        if (_closed.IsCancellationRequested)
        {
            return;
        }
        StatusText.Text = message;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "TextPrimary");
    }

    private void UpdateRequestButton()
    {
        RequestButton.IsEnabled = !_requesting && CandidatesGrid.SelectedItem is CandidateRow { Candidate.Eligible: true };
        RequestButton.Content = _requesting ? "等待对方同意…" : "向选中的朋友申请";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Candidate_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRequestButton();
    private async void Accept_Click(object sender, RoutedEventArgs e) => await RespondAsync(sender, true);
    private async void Decline_Click(object sender, RoutedEventArgs e) => await RespondAsync(sender, false);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record CandidateRow(PeerAssistCandidate Candidate)
    {
        public string Name => Candidate.Relay.Nickname;
        public string ToRelayText => FormatQuality(Candidate.ToRelay);
        public string ToTargetText => FormatQuality(Candidate.RelayToTarget);
        public string EvidenceText => $"你 → 朋友：{FormatEvidence(Candidate.ToRelay)}\n朋友 → 对象：{FormatEvidence(Candidate.RelayToTarget)}";
        public string Status => Candidate.Status;

        private static string FormatQuality(LinkQualitySnapshot? quality) => quality is null ? "待测量" :
            $"{(quality.RttMs is { } rtt ? $"{rtt:0} ms" : "—")} / {(quality.LossPercent is { } loss ? $"{loss:0.#}%" : "—")}";

        private static string FormatEvidence(LinkQualitySnapshot? quality) => quality is null ? "暂无报告" :
            $"{quality.Samples} 次 / {(quality.CurrentAgeSeconds < 1 ? "刚刚" : $"{quality.CurrentAgeSeconds:0} 秒前")}";
    }

    private sealed record SessionRow(PeerAssistSession Session)
    {
        public string CircuitId => Session.CircuitId;
        public string Description => Session.Description;
        public string StateText => $"{(Session.IsRelay ? "你正在协助朋友" : "你的通信协助")} · {(Session.Active ? "转发中" : "尚未启用转发，请查看会话说明")}";
    }
}

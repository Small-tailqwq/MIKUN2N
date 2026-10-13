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
            if (target is null)
            {
                RefreshSessionsAndRequests();
                AssistTabs.SelectedItem = RequestsTab;
            }
            return;
        }
        _target = target;
        _targetRevision++;
        TargetText.Text = target is null
            ? ""
            : $"联机对象：{target.Nickname}";
        TargetText.Visibility = target is null ? Visibility.Collapsed : Visibility.Visible;
        AskIntroText.Text = target is null ? "" :
            "选择一位朋友，对方同意后即可帮你们转发。";
        AskTab.Visibility = target is null ? Visibility.Collapsed : Visibility.Visible;
        AssistTabs.SelectedItem = target is null ? RequestsTab : AskTab;
        CandidatesList.ItemsSource = null;
        CandidatesEmptyText.Text = "正在测量其他朋友的线路…";
        CandidatesEmptyText.Visibility = Visibility.Visible;
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
                var selected = (CandidatesList.SelectedItem as CandidateRow)?.Candidate.Relay.NodeId;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                var candidates = await _controller.GetPeerAssistCandidatesAsync(target.NodeId, timeout.Token);
                if (_closed.IsCancellationRequested || revision != _targetRevision)
                {
                    return;
                }
                // Candidates arrive best first, so the first eligible one with enough samples
                // is the recommendation; with too few samples nothing is recommended.
                var recommended = candidates.FirstOrDefault(candidate => candidate.Eligible &&
                    candidate.ToRelay?.HasEnoughSamples == true && candidate.RelayToTarget?.HasEnoughSamples == true);
                var rows = candidates.Select(candidate =>
                    new CandidateRow(candidate, target.Nickname, ReferenceEquals(candidate, recommended))).ToArray();
                CandidatesList.ItemsSource = rows;
                CandidatesList.SelectedItem =
                    rows.FirstOrDefault(row => row.Candidate.Eligible && row.Candidate.Relay.NodeId == selected) ??
                    (selected is null ? rows.FirstOrDefault(row => row.Recommended) : null);
                CandidatesEmptyText.Text = "暂时没有其他在线的朋友可以帮忙。";
                CandidatesEmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        RequestsTab.Header = requests.Count == 0 ? "收到的申请" : $"收到的申请（{requests.Count}）";
        var sessions = _controller.GetPeerAssistSessions().Select(session => new SessionRow(session)).ToArray();
        SessionsList.ItemsSource = sessions;
        SessionsEmptyText.Visibility = sessions.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SessionsTab.Header = sessions.Length == 0 ? "进行中" : $"进行中（{sessions.Length}）";
        RequestsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void Request_Click(object sender, RoutedEventArgs e)
    {
        if (_requesting || _target is not { } target || CandidatesList.SelectedItem is not CandidateRow { Candidate.Eligible: true } row)
        {
            return;
        }
        _requesting = true;
        UpdateRequestButton();
        SetStatus($"已向 {row.Name} 发出申请，等对方同意…");
        try
        {
            await _controller.RequestPeerAssistAsync(target.NodeId, row.Candidate.Relay.NodeId, _closed.Token);
            if (!_closed.IsCancellationRequested)
            {
                SetStatus($"{row.Name} 已同意，正在确认线路…");
                AssistTabs.SelectedItem = SessionsTab;
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
            SetStatus(accept ? "已同意，正在确认线路…" : "已拒绝申请。");
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
            SetStatus("已结束协助，恢复原线路。");
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
        StatusText.Visibility = Visibility.Visible;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "TextPrimary");
    }

    private void UpdateRequestButton()
    {
        var row = CandidatesList.SelectedItem as CandidateRow;
        RequestButton.IsEnabled = !_requesting && row is { Candidate.Eligible: true };
        RequestButton.Content = _requesting ? "等待对方同意…" :
            row is { Candidate.Eligible: true } ? $"请 {row.Name} 帮忙" : "选择一位朋友";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Candidate_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRequestButton();
    private async void Accept_Click(object sender, RoutedEventArgs e) => await RespondAsync(sender, true);
    private async void Decline_Click(object sender, RoutedEventArgs e) => await RespondAsync(sender, false);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record CandidateRow(PeerAssistCandidate Candidate, string TargetName, bool Recommended)
    {
        public string Name => Candidate.Relay.Nickname;
        public Visibility RecommendedVisibility => Recommended ? Visibility.Visible : Visibility.Collapsed;
        public string RouteText => $"你 → {Name}  {FormatRtt(Candidate.ToRelay)}    ·    {Name} → {TargetName}  {FormatRtt(Candidate.RelayToTarget)}";

        private bool Measured => Candidate.EstRttMs is not null && Candidate.EstLossPercent is not null;
        private bool EnoughSamples => Candidate.ToRelay?.HasEnoughSamples == true && Candidate.RelayToTarget?.HasEnoughSamples == true;

        // Bands are a reading aid for the summed two-leg estimate, not a promise about game traffic.
        public string QualityTone => !Candidate.Eligible || !Measured || !EnoughSamples ? "none" :
            Candidate.EstLossPercent <= 1 && Candidate.EstRttMs <= 80 ? "good" :
            Candidate.EstLossPercent <= 5 && Candidate.EstRttMs <= 150 ? "fair" : "poor";

        public string QualityLabel => !Candidate.Eligible ? "暂时不能帮忙" : QualityTone switch
        {
            "good" => "线路很好",
            "fair" => "线路一般",
            "poor" => "线路较差",
            _ => "还在测量"
        };

        public string SummaryText
        {
            get
            {
                if (!Candidate.Eligible)
                {
                    return $"原因：{Candidate.Status}";
                }
                if (!Measured)
                {
                    return "暂无测量，可先申请。";
                }
                var estimate = $"合计约 {Candidate.EstRttMs:0} ms · 探测未响应 {Candidate.EstLossPercent:0.#}%";
                return EnoughSamples ? estimate : estimate + " · 样本较少";
            }
        }

        public string Details => $"你 → {Name}：{FormatDetail(Candidate.ToRelay)}\n{Name} → {TargetName}：{FormatDetail(Candidate.RelayToTarget)}\n" +
            "近期探测结果仅供线路比较，不代表实际游戏延迟或丢包率。";

        private static string FormatRtt(LinkQualitySnapshot? quality) =>
            quality?.RttMs is { } rtt ? $"{rtt:0} ms" : "待测量";

        private static string FormatDetail(LinkQualitySnapshot? quality) => quality is null ? "暂无测量" :
            $"延迟 {(quality.RttMs is { } rtt ? $"{rtt:0} ms" : "—")}，未响应 {(quality.LossPercent is { } loss ? $"{loss:0.#}%" : "—")}，" +
            $"{quality.Samples} 次测量，{(quality.CurrentAgeSeconds < 1 ? "刚刚" : $"{quality.CurrentAgeSeconds:0} 秒前")}更新";
    }

    private sealed record SessionRow(PeerAssistSession Session)
    {
        public string CircuitId => Session.CircuitId;
        public string Description => Session.Description;
        public string RoleText => Session.IsRelay ? "你在帮朋友转发" : "朋友在帮你转发";
        public string StateLabel => Session.Active ? "转发中" : "确认中";
        public string StateTone => Session.Active ? "good" : "none";
    }
}

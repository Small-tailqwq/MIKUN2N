using System.Diagnostics;
using MikuN2N.Models;

namespace MikuN2N.Services;

internal sealed class PeerLinkQuality
{
    private const int WindowSeconds = 60;
    private readonly object _gate = new();
    private readonly List<Sample> _samples = new();

    public void Record(long sentAt, double? rttMs)
    {
        lock (_gate)
        {
            Trim(Stopwatch.GetTimestamp());
            _samples.Add(new(sentAt, rttMs));
            // Bound memory even if the probe cadence is accidentally shortened.
            if (_samples.Count > 64)
                _samples.Remove(_samples.MinBy(sample => sample.SentAt)!);
        }
    }

    public LinkQualitySnapshot Snapshot(string nodeId, PeerConnectionMode mode)
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            Trim(now);
            var ordered = _samples.OrderBy(sample => sample.SentAt).ToArray();
            var rtts = ordered.Where(sample => sample.RttMs.HasValue).Select(sample => sample.RttMs!.Value).ToArray();
            var sorted = rtts.Order().ToArray();
            var lost = ordered.Length - rtts.Length;
            var jitter = rtts.Length >= 2
                ? rtts.Skip(1).Select((value, index) => Math.Abs(value - rtts[index])).Average()
                : (double?)null;
            return new(nodeId, ordered.Length, lost,
                rtts.Length == 0 ? null : rtts.Average(), jitter,
                ordered.Length == 0 ? null : 100.0 * lost / ordered.Length,
                ordered.Length == 0 ? WindowSeconds : Stopwatch.GetElapsedTime(ordered[^1].SentAt, now).TotalSeconds,
                mode, sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1],
                WindowSeconds);
        }
    }

    private void Trim(long now) =>
        _samples.RemoveAll(sample => Stopwatch.GetElapsedTime(sample.SentAt, now).TotalSeconds > WindowSeconds);

    private sealed record Sample(long SentAt, double? RttMs);
}

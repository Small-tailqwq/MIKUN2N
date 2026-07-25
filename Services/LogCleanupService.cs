using System.IO;
using Timer = System.Threading.Timer;

namespace MikuN2N.Services;

public sealed class LogCleanupService : IDisposable
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);
    private readonly string _logDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MikuN2N",
        "logs");
    private readonly Timer _timer;
    private int _retentionDays = 30;
    private int _cleanupRunning;

    public LogCleanupService()
    {
        _timer = new Timer(_ => Cleanup(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Configure(int retentionDays)
    {
        _retentionDays = retentionDays is 0 or 7 or 30 or 90 or 180
            ? retentionDays
            : 30;
        _timer.Change(TimeSpan.Zero, CleanupInterval);
    }

    public void Dispose() => _timer.Dispose();

    private void Cleanup()
    {
        if (_retentionDays == 0 || Interlocked.Exchange(ref _cleanupRunning, 1) != 0)
        {
            return;
        }

        try
        {
            if (!Directory.Exists(_logDirectory))
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
            foreach (var path in Directory.EnumerateFiles(
                         _logDirectory,
                         "*",
                         SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        File.Delete(path);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            Volatile.Write(ref _cleanupRunning, 0);
        }
    }
}

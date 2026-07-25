using System.IO;
using System.Text;

namespace MikuN2N.Services;

/// <summary>
/// Records exceptions that escaped their handler. Without this an async void slip
/// only ever surfaces in the Windows event log, where the user sees a several-second
/// hang and nothing else.
/// </summary>
public static class CrashLogService
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MikuN2N",
        "logs",
        "crash.log");

    public static string LogPath => FilePath;

    public static void Record(string source, Exception? exception, bool fatal)
    {
        try
        {
            var report = new StringBuilder();
            report.AppendLine(new string('=', 78));
            report.AppendLine(
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] " +
                $"MikuN2N {BuildIdentity.Version} / {(fatal ? "进程终止" : "已捕获")} / {source}");
            report.AppendLine($"运行环境：{Environment.OSVersion} / .NET {Environment.Version} / 64 位进程={Environment.Is64BitProcess}");

            var current = exception;
            var depth = 0;
            while (current is not null)
            {
                report.AppendLine($"--- 异常 {depth}：{current.GetType().FullName}");
                report.AppendLine($"消息：{current.Message}");
                report.AppendLine(current.StackTrace ?? "（无堆栈）");
                current = current.InnerException;
                depth++;
            }

            if (exception is null)
            {
                report.AppendLine("（没有异常对象，运行时只报告了终止事件）");
            }

            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, report.ToString(), new UTF8Encoding(false));
            }
        }
        catch
        {
            // A failing crash logger must never replace the crash it is reporting.
        }
    }
}

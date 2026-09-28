using System.Runtime.InteropServices;
using WpfClipboard = System.Windows.Clipboard;

namespace MikuN2N.Services;

public static class ClipboardService
{
    /// <summary>
    /// Clipboard.SetText flushes right after setting, and clipboard history, chat apps and
    /// remote desktop open the clipboard as soon as they are notified of the change, so the
    /// flush fails with CLIPBRD_E_CANT_OPEN although the text is already there. Setting
    /// without the flush does not race them (WPF already retries opening the clipboard);
    /// the best-effort flush only keeps the text available after the program exits.
    /// </summary>
    public static bool TrySetText(string text)
    {
        try
        {
            WpfClipboard.SetDataObject(text, false);
        }
        catch (COMException)
        {
            return HoldsText(text);
        }
        try
        {
            WpfClipboard.Flush();
        }
        catch (COMException)
        {
        }
        return true;
    }

    private static bool HoldsText(string text)
    {
        try
        {
            return WpfClipboard.ContainsText() && WpfClipboard.GetText() == text;
        }
        catch (COMException)
        {
            return false;
        }
    }
}

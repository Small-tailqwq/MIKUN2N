using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace MikuN2N.Services;

internal static class NotificationRegistration
{
    // Keep the Start menu identity aligned with the process, including portable folder moves.
    // This registers our app, not the user's notification preferences or quiet-hours settings.
    public static bool TryRegister()
    {
        object? shortcut = null;
        try
        {
            if (Environment.ProcessPath is not { } executable) return false;
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (string.IsNullOrEmpty(programs)) return false;
            Directory.CreateDirectory(programs);
            var path = Path.Combine(programs, "MikuN2N.lnk");
            shortcut = new ShellLink();
            var link = (IShellLink)shortcut;
            var file = (IPersistFile)shortcut;
            var store = (IPropertyStore)shortcut;
            var appIdKey = new PropertyKey
            {
                FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
                PropertyId = 5
            };
            if (File.Exists(path))
            {
                file.Load(path, 0);
                store.GetValue(ref appIdKey, out var existingId);
                try
                {
                    // Do not replace a user-created shortcut or another app with the same name.
                    if (existingId.Type != 31 || Marshal.PtrToStringUni(existingId.Pointer) != AppIconService.AppUserModelId)
                        return false;
                }
                finally { PropVariantClear(ref existingId); }
                var target = new StringBuilder(32768);
                link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
                if (string.Equals(target.ToString(), executable, StringComparison.OrdinalIgnoreCase)) return true;
            }
            link.SetPath(executable);
            link.SetWorkingDirectory(Path.GetDirectoryName(executable)!);
            link.SetDescription("MikuN2N");
            link.SetIconLocation(executable, 0);
            var appId = new PropVariant
            {
                Type = 31,
                Pointer = Marshal.StringToCoTaskMemUni(AppIconService.AppUserModelId)
            };
            try { store.SetValue(ref appIdKey, ref appId); }
            finally { PropVariantClear(ref appId); }
            store.Commit();
            file.Save(path, true);
            return true;
        }
        catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemList);
        void SetIDList(IntPtr itemList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int length);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int length);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCommand(out int command);
        void SetShowCommand(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}

using System.Runtime.InteropServices;

namespace MyApp.Desktop.Services;

internal sealed class TrayIcon : IDisposable
{
    private const int NimAdd = 0x00000000;
    private const int NimDelete = 0x00000002;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;
    private const int ImageIcon = 1;
    private const int LrLoadFromFile = 0x00000010;

    private readonly NotifyIconData _data;
    private readonly nint _iconHandle;
    private bool _disposed;

    public TrayIcon(nint windowHandle, string iconPath, string tooltip)
    {
        _iconHandle = LoadImage(nint.Zero, iconPath, ImageIcon, 0, 0, LrLoadFromFile);
        if (_iconHandle == nint.Zero)
        {
            return;
        }

        _data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = windowHandle,
            uID = 1,
            uFlags = NifIcon | NifTip,
            hIcon = _iconHandle,
            szTip = tooltip
        };

        ShellNotifyIcon(NimAdd, ref _data);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        var data = _data;
        ShellNotifyIcon(NimDelete, ref data);

        if (_iconHandle != nint.Zero)
        {
            DestroyIcon(_iconHandle);
        }

        _disposed = true;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    private static extern bool ShellNotifyIcon(int dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint hInst, string name, int type, int cx, int cy, int fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }
}

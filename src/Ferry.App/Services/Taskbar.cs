using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Ferry.App.Services;

/// <summary>The window's taskbar button: download progress on it, and a flash when a batch finishes in the background.</summary>
public sealed partial class Taskbar(nint window)
{
    private ITaskbarList3? _list;

    public enum State
    {
        None = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8,
    }

    /// <param name="fraction">0 to 1</param>
    public void Show(State state, double fraction = 0)
    {
        var list = List();
        if (list is null)
            return;
        list.SetProgressState(window, (int)state);
        if (state is State.Normal or State.Error or State.Paused)
            list.SetProgressValue(window, (ulong)(Math.Clamp(fraction, 0, 1) * 1000), 1000);
    }

    /// <summary>Whether the window is the one the user is in.</summary>
    public bool IsInFront => GetForegroundWindow() == window;

    /// <summary>Flash the button a few times, unless the window is in front.</summary>
    public void Flash()
    {
        if (IsInFront)
            return;
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = window,
            Flags = 0x3 | 0xC, // caption and tray, until the window comes to the front
            Count = 3,
        };
        FlashWindowEx(ref info);
    }

    private ITaskbarList3? List()
    {
        if (_list is not null)
            return _list;
        try
        {
            var created = (ITaskbarList3)new CTaskbarList();
            created.HrInit();
            return _list = created;
        }
        catch (COMException)
        {
            return null; // no taskbar (a server session without Explorer): nothing to show
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashInfo info);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class CTaskbarList;

    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(nint hwnd);
        void DeleteTab(nint hwnd);
        void ActivateTab(nint hwnd);
        void SetActiveAlt(nint hwnd);
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(nint hwnd, ulong completed, ulong total);
        void SetProgressState(nint hwnd, int flags);
    }
}

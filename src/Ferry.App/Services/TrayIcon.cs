using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Ferry.App.Services;

/// <summary>
/// Ferry in the notification area: click to open, right-click for a menu, and notes on what finished. Windows shows
/// those notes as its own notifications, with no registration an unpackaged app would need otherwise.
/// </summary>
public sealed unsafe partial class TrayIcon : IDisposable
{
    private const nuint SubclassId = 2;
    private const uint IconId = 1;
    private const uint CallbackMessage = 0x8001; // WM_APP + 1
    private const uint Add = 0, Modify = 1, Delete = 2;
    private const uint HasMessage = 0x1, HasIcon = 0x2, HasTip = 0x4, HasInfo = 0x10;
    private const uint RespectQuietTime = 0x80;
    private const nint LeftButtonUp = 0x202, RightButtonUp = 0x205, BalloonClicked = 0x405;
    private const uint LoadFromFile = 0x10, IconImage = 1;
    private const int SmallIconSize = 49; // SM_CXSMICON
    private const uint ReturnCommand = 0x100, RightAlign = 0x8;
    private const int OpenCommand = 1, PauseAllCommand = 2, ResumeAllCommand = 3, QuitCommand = 4;

    private readonly nint _window;
    private readonly nint _icon;
    private readonly uint _taskbarCreated;
    private readonly DispatcherQueue _thread = DispatcherQueue.GetForCurrentThread();
    private GCHandle _self;

    /// <param name="iconFile">the .ico to show; without it there is no icon, and the notes don't show</param>
    public TrayIcon(nint window, string iconFile)
    {
        _window = window;
        _icon = LoadImage(0, iconFile, IconImage, GetSystemMetrics(SmallIconSize), GetSystemMetrics(SmallIconSize), LoadFromFile);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _self = GCHandle.Alloc(this);
        _ = SetWindowSubclass(window, &Proc, SubclassId, GCHandle.ToIntPtr(_self));
        Show();
    }

    public event Action? Open;

    public event Action? PauseAll;

    public event Action? ResumeAll;

    public event Action? Quit;

    /// <summary>A note from Ferry; clicking it opens the window.</summary>
    public void Notify(string title, string text)
    {
        if (_icon == 0)
            return;
        var data = NewData(HasInfo);
        Copy(title, new Span<char>(data.InfoTitle, TitleChars));
        Copy(text, new Span<char>(data.Info, InfoChars));
        data.InfoFlags = RespectQuietTime;
        _ = Shell_NotifyIcon(Modify, &data);
    }

    public void Dispose()
    {
        var data = NewData(0);
        _ = Shell_NotifyIcon(Delete, &data);
        _ = RemoveWindowSubclass(_window, &Proc, SubclassId);
        if (_self.IsAllocated)
            _self.Free();
        if (_icon != 0)
            _ = DestroyIcon(_icon);
    }

    private void Show()
    {
        if (_icon == 0)
            return;
        var data = NewData(HasMessage | HasIcon | HasTip);
        data.CallbackMessage = CallbackMessage;
        data.Icon = _icon;
        Copy(Core.Product.Name, new Span<char>(data.Tip, TipChars));
        _ = Shell_NotifyIcon(Add, &data);
    }

    private NotifyIconData NewData(uint flags) => new()
    {
        Size = (uint)sizeof(NotifyIconData),
        Window = _window,
        Id = IconId,
        Flags = flags,
    };

    /// <summary>As much as fits; what is left of the buffer is zeros, which end the text.</summary>
    private static void Copy(string text, Span<char> buffer) =>
        text.AsSpan(0, Math.Min(text.Length, buffer.Length - 1)).CopyTo(buffer);

    [UnmanagedCallersOnly]
    private static nint Proc(nint window, uint message, nint wParam, nint lParam, nuint id, nint data)
    {
        if (GCHandle.FromIntPtr(data).Target is TrayIcon tray)
        {
            if (message == CallbackMessage)
                tray.OnIconMessage(lParam);
            else if (message == tray._taskbarCreated)
                tray.Show(); // Explorer restarted, and took the icon with it
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    // after the message, not inside it: the menu runs a message loop, and the handlers open windows
    private void OnIconMessage(nint message)
    {
        if (message is LeftButtonUp or BalloonClicked)
            _thread.TryEnqueue(() => Open?.Invoke());
        else if (message == RightButtonUp)
            _thread.TryEnqueue(ShowMenu);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        _ = AppendMenu(menu, 0, OpenCommand, $"Open {Core.Product.Name}");
        _ = AppendMenu(menu, 0, PauseAllCommand, "Pause all");
        _ = AppendMenu(menu, 0, ResumeAllCommand, "Resume all");
        _ = AppendMenu(menu, 0, QuitCommand, "Quit");
        _ = GetCursorPos(out var at);
        _ = SetForegroundWindow(_window); // or the menu stays open after a click elsewhere
        var chosen = TrackPopupMenu(menu, ReturnCommand | RightAlign, at.X, at.Y, 0, _window, 0);
        _ = DestroyMenu(menu);
        var handler = chosen switch
        {
            OpenCommand => Open,
            PauseAllCommand => PauseAll,
            ResumeAllCommand => ResumeAll,
            QuitCommand => Quit,
            _ => null,
        };
        handler?.Invoke();
    }

    private const int TipChars = 128, InfoChars = 256, TitleChars = 64;

    /// <summary>NOTIFYICONDATAW.</summary>
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public fixed char Tip[TipChars];
        public uint State;
        public uint StateMask;
        public fixed char Info[InfoChars];
        public uint Version;
        public fixed char InfoTitle[TitleChars];
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIcon(uint message, NotifyIconData* data);

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessage(string name);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint window, delegate* unmanaged<nint, uint, nint, nint, nuint, nint, nint> proc,
                                                  nuint id, nint data);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint window, delegate* unmanaged<nint, uint, nint, nint, nuint, nint, nint> proc, nuint id);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenu(nint menu, uint flags, nuint id, string text);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rectangle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);
}

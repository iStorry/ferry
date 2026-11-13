using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Ferry.App.Services;

/// <summary>Raises <see cref="Changed"/> on the window's thread after every copy, in any program, while the window is open.</summary>
/// <remarks>WinRT's Clipboard.ContentChanged only fires while the window has focus, and links are copied in a browser;
/// WM_CLIPBOARDUPDATE comes either way.</remarks>
public sealed unsafe partial class ClipboardWatcher
{
    private const uint ClipboardUpdate = 0x031D;
    private readonly DispatcherQueue _thread = DispatcherQueue.GetForCurrentThread();

    public ClipboardWatcher(nint window)
    {
        var self = GCHandle.Alloc(this); // lives as long as the window, which is as long as the app
        if (SetWindowSubclass(window, &Proc, 1, GCHandle.ToIntPtr(self)))
            AddClipboardFormatListener(window);
    }

    public event Action? Changed;

    [UnmanagedCallersOnly]
    private static nint Proc(nint window, uint message, nint wParam, nint lParam, nuint id, nint data)
    {
        if (message == ClipboardUpdate && GCHandle.FromIntPtr(data).Target is ClipboardWatcher watcher)
            watcher._thread.TryEnqueue(() => watcher.Changed?.Invoke()); // read the clipboard after the message, not inside it
        return DefSubclassProc(window, message, wParam, lParam);
    }

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint window, delegate* unmanaged<nint, uint, nint, nint, nuint, nint, nint> proc,
                                                  nuint id, nint data);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AddClipboardFormatListener(nint window);
}

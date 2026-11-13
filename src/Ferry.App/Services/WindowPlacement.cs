using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Ferry.App.Services;

/// <summary>The window's size and position, remembered in the settings file's app state between runs.</summary>
public static partial class WindowPlacement
{
    private const string Key = "window";
    private const int DefaultWidth = 1000;
    private const int DefaultHeight = 680;
    private const int MinWidth = 600;
    private const int MinHeight = 500;

    /// <summary>Put the window where it was, if that is still on a screen; otherwise 1000×680 in the middle.</summary>
    public static void Restore(AppWindow window, nint hwnd, AppServices app)
    {
        var scale = GetDpiForWindow(hwnd) / 96.0;
        if (window.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(MinWidth * scale);
            presenter.PreferredMinimumHeight = (int)(MinHeight * scale);
        }
        var saved = app.State(Key)?.Split(',');
        if (saved is [var x, var y, var w, var h, var maximized]
            && int.TryParse(x, CultureInfo.InvariantCulture, out var left) && int.TryParse(y, CultureInfo.InvariantCulture, out var top)
            && int.TryParse(w, CultureInfo.InvariantCulture, out var width) && int.TryParse(h, CultureInfo.InvariantCulture, out var height))
        {
            var rect = new RectInt32(left, top, Math.Max(width, (int)(MinWidth * scale)), Math.Max(height, (int)(MinHeight * scale)));
            var area = DisplayArea.GetFromRect(rect, DisplayAreaFallback.None);
            if (area is not null)
            {
                window.MoveAndResize(rect);
                if (maximized == "1" && window.Presenter is OverlappedPresenter overlapped)
                    overlapped.Maximize();
                return;
            }
        }
        var size = new SizeInt32((int)(DefaultWidth * scale), (int)(DefaultHeight * scale));
        var work = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Primary).WorkArea;
        window.MoveAndResize(new RectInt32(work.X + (work.Width - size.Width) / 2, work.Y + (work.Height - size.Height) / 2, size.Width, size.Height));
    }

    public static void Save(AppWindow window, AppServices app)
    {
        var maximized = window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        if (window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            return;
        var (position, size) = (window.Position, window.Size);
        app.SetState(Key, string.Join(',', position.X, position.Y, size.Width, size.Height, maximized ? 1 : 0));
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);
}

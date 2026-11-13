using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ferry.App.Services;

/// <summary>
/// What to do once the queue has finished. Chosen for one batch and never saved: a shutdown left on from last week
/// mustn't end a later session.
/// </summary>
public enum WhenDone
{
    Nothing,
    Close,
    Sleep,
    ShutDown,
}

public static partial class WhenDoneActions
{
    public static string Label(WhenDone action) => action switch
    {
        WhenDone.Close => "Close Ferry",
        WhenDone.Sleep => "Put this PC to sleep",
        WhenDone.ShutDown => "Shut down this PC",
        _ => "Do nothing",
    };

    /// <summary>The label as it reads in the middle of a sentence.</summary>
    public static string InSentence(WhenDone action)
    {
        var label = Label(action);
        return char.ToLowerInvariant(label[0]) + label[1..];
    }

    /// <summary>Sleep or shut down; false, with the reason logged, when Windows wouldn't.</summary>
    public static bool RunOnPc(WhenDone action)
    {
        try
        {
            if (action == WhenDone.Sleep)
                return SetSuspendState(hibernate: false, force: false, wakeEventsDisabled: false);
            if (action == WhenDone.ShutDown)
            {
                using var shutdown = Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
                return shutdown is not null;
            }
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            CrashLog.Write($"when done: {action}", e);
            return false;
        }
    }

    [LibraryImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool force,
                                                [MarshalAs(UnmanagedType.U1)] bool wakeEventsDisabled);
}

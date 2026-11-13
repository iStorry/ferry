using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ferry.App;

/// <summary>
/// Ferry is already running from another folder (an installed copy and a portable or newly built one): say where, and
/// that it has to close first. Shown before any window exists, so a plain message box.
/// </summary>
internal static partial class OtherCopy
{
    private const uint MbOk = 0x0;
    private const uint MbIconInformation = 0x40;
    private const uint MbSetForeground = 0x10000;

    public static void Tell()
    {
        var where = Where();
        _ = MessageBox(0,
            $"Ferry is already running{(where is null ? "" : $" from {where}")}.\n\n" +
            "Close it first, or use that one: two copies would each keep their own queue over the same files.",
            "Ferry", MbOk | MbIconInformation | MbSetForeground);
    }

    /// <summary>The folder the other Ferry runs from, when it can be found.</summary>
    private static string? Where()
    {
        var self = Environment.ProcessPath;
        foreach (var process in Process.GetProcessesByName("Ferry"))
        {
            using (process)
            {
                try
                {
                    if (process.Id != Environment.ProcessId && process.MainModule?.FileName is { } path
                        && !string.Equals(path, self, StringComparison.OrdinalIgnoreCase))
                        return Path.GetDirectoryName(path);
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // gone, or not ours to look at
                }
            }
        }
        return null;
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint owner, string text, string caption, uint type);
}

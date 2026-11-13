using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ferry.Core.Files;

/// <summary>
/// A file written at offsets far apart, as a download in parts is. NTFS keeps a "valid data length" and fills
/// everything between it and a write with zeros first, so the last part's first write would write most of the file
/// as zeros while the other connections wait on the same handle. A sparse file has no such fill; ext4 and the other
/// Linux file systems leave holes anyway.
/// </summary>
internal static partial class SparseFile
{
    private const uint FsctlSetSparse = 0x000900C4;

    /// <summary>
    /// Mark the file open on <paramref name="handle"/> as sparse; false when that can't be done. Any failure only costs
    /// speed: the file still downloads, with the fill. The handle must be a synchronous one: the request is not overlapped.
    /// </summary>
    public static bool TryMark(SafeFileHandle handle) =>
        OperatingSystem.IsWindows() && DeviceIoControl(handle, FsctlSetSparse, nint.Zero, 0, nint.Zero, 0, out _, nint.Zero);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, nint input, uint inputSize, nint output, uint outputSize,
                                                out uint returned, nint overlapped);
}

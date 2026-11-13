using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ferry.Plugins.Server;

/// <summary>
/// A Windows job object that kills every process in it when this process ends: its handle is never closed, so
/// Windows closes it at exit (a crash and a kill from Task Manager included), and the processes go with it. Every ssh
/// Ferry starts is put in it.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class KillOnCloseJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;
    private static readonly Lazy<nint> Job = new(Create);

    /// <summary>Put <paramref name="process"/> in the job; when that can't be done it runs without, killed on dispose only.</summary>
    public static void Add(Process process)
    {
        var job = Job.Value;
        if (job == 0)
            return;
        try
        {
            _ = AssignProcessToJobObject(job, process.Handle); // refused (a job of ours that forbids it): killed on dispose only
        }
        catch (InvalidOperationException)
        {
            // it has exited already
        }
    }

    private static nint Create()
    {
        var job = CreateJobObject(0, 0);
        if (job == 0)
            return 0;
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnJobClose } };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<ExtendedLimits>()))
        {
            CloseHandle(job);
            return 0; // a job that wouldn't kill is no use
        }
        return job;
    }

    // JOBOBJECT_BASIC_LIMIT_INFORMATION
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    // IO_COUNTERS
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    // JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static partial nint CreateJobObject(nint attributes, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimits info, int length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

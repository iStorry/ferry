namespace Ferry.Core.Sites;

/// <summary>What a transport is given to work with.</summary>
/// <param name="PartPath">where the bytes go until the file is complete, a file or a folder of files; what is there already is from an earlier try, to carry on from if it can</param>
/// <param name="OnBytes">told how many bytes of the file are there now</param>
/// <param name="Pace">to wait after this many bytes, so the speed limits hold</param>
/// <param name="OnTotal">told the file's size, when that is only known once it has started</param>
/// <param name="OnEnding">told what to add to the file's name, when what it turned out to be only shows by the end (".tar")</param>
public sealed record TransportJob(string PartPath, Action<long> OnBytes, Func<int, CancellationToken, ValueTask> Pace, Action<long> OnTotal, Action<string> OnEnding);

/// <summary>Brings a file over some other way than a web download: another protocol, or bytes that need work on the way.</summary>
public interface IFileTransport
{
    /// <summary>Returns once the whole file is at <see cref="TransportJob.PartPath"/>.</summary>
    /// <exception cref="StageException">it failed; retryable if another try may do better</exception>
    Task TransferAsync(TransportJob job, CancellationToken cancel);
}

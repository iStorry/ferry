using Ferry.Core.Http;
using Ferry.Core.Sites;

namespace Ferry.Core.Download;

/// <param name="Folder">where the file goes; created if needed</param>
/// <param name="Name">save under this name instead of the server's (made safe like any other)</param>
/// <param name="Limit">this file's own speed limit; its rate can change while the file downloads</param>
/// <param name="Details">what to send with every request for this file</param>
/// <param name="FolderChosen">the user chose <paramref name="Folder"/> for this file, so it isn't sorted by type</param>
/// <param name="Sha256">what the finished file must hash to, in lower-case hex</param>
public sealed record DownloadRequest(FileLink Link, string Folder, string? Name = null, SpeedLimit? Limit = null,
                                     RequestDetails? Details = null, bool FolderChosen = false, string? Sha256 = null);

/// <param name="Path">the file written, or the existing file that was skipped</param>
/// <param name="Sha256">the SHA-256 of the file written, in lower-case hex, hashed as it arrived, when something needed it
/// (a checksum, or <see cref="DownloadOptions.AlwaysHash"/>); null otherwise, and for a file that was skipped or that a
/// site's own transport brought over</param>
public sealed record DownloadResult(string Path, bool Skipped, string? Sha256 = null);

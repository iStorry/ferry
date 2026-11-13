using Ferry.Core.Files;
using Ferry.Core.Settings;
using Ferry.Core;
using System.Security.Cryptography;

namespace Ferry.Plugins.Server;

/// <summary>A server's host key, as a known_hosts line holds it.</summary>
/// <param name="Hosts">the host pattern ssh wrote ("midas.example", or "[midas.example]:2222" off port 22)</param>
/// <param name="Type">"ssh-ed25519", "ecdsa-sha2-nistp256", "ssh-rsa"</param>
/// <param name="Key">the key blob, base64</param>
public sealed record HostKey(string Hosts, string Type, string Key)
{
    /// <summary>"SHA256:…", as ssh and the setup show it.</summary>
    public string Fingerprint => KnownHosts.Fingerprint(Key);

    public string Line => $"{Hosts} {Type} {Key}";

    /// <summary>A known_hosts line, or null for a comment, a blank, a marker ("@revoked") or something else.</summary>
    public static HostKey? Parse(string line)
    {
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || parts[0].StartsWith('#') || parts[0].StartsWith('@'))
            return null;
        try
        {
            _ = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return null;
        }
        return new HostKey(parts[0], parts[1], parts[2]);
    }
}

/// <summary>
/// Ferry's own known_hosts (docs/openssh-plan.md §4.5): one line, the key the user confirmed. The user's
/// ~/.ssh/known_hosts is never read or written. A server's key is recorded by ssh itself, with no login offered:
/// ssh-keyscan for Windows (9.5p2) can't do the key exchange it offers to an OpenSSH 10 server.
/// </summary>
public static class KnownHosts
{
    private static readonly TimeSpan RecordWait = TimeSpan.FromSeconds(30);

    /// <summary>"SHA256:" and the base64 of the SHA-256 of the key blob, without padding, as ssh shows it.</summary>
    public static string Fingerprint(string base64Key) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(base64Key))).TrimEnd('=');

    /// <summary>
    /// Connect to the server only to learn its host key: ssh records the key it negotiated into a scratch file, offers
    /// no login, and ends with "Permission denied", which is expected.
    /// </summary>
    /// <exception cref="StageException">the server can't be reached, or ssh recorded nothing (<see cref="Stage.Server"/>)</exception>
    public static async Task<HostKey> RecordAsync(SshTools tools, ServerProfile profile, CancellationToken cancel)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"ferry-hostkey-{Guid.NewGuid():N}");
        try
        {
            var target = new SshTarget(profile.Host, profile.Port, profile.User, KeyFile: "", scratch, HostKeyType: null, RecordWait);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            wait.CancelAfter(RecordWait);
            SshResult result;
            try
            {
                result = await SshProcess.RunAsync(tools.Ssh, SshArguments.For(target, SshLogin.RecordHostKey, "true"), null, wait.Token);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new StageException(Stage.Server, $"{profile.Display} didn't answer within {RecordWait.TotalSeconds:0} s.", isRetryable: true);
            }
            if (Read(scratch) is [var recorded, ..])
                return recorded;
            var failure = SshErrors.Classify(result.Errors);
            throw failure.Kind == SshFailureKind.Unreachable
                ? SshErrors.ToException(failure, profile.Display, null)
                : new StageException(Stage.Server, $"{profile.Display} gave no host key: {failure.Detail}", isRetryable: true);
        }
        finally
        {
            File.Delete(scratch);
        }
    }

    /// <summary>The keys in a known_hosts file; none when it doesn't exist.</summary>
    public static IReadOnlyList<HostKey> Read(string path) =>
        File.Exists(path) ? [.. File.ReadAllLines(path).Select(HostKey.Parse).OfType<HostKey>()] : [];

    /// <summary>Make <paramref name="path"/> hold <paramref name="key"/> alone, private to this user.</summary>
    public static void Save(string path, HostKey key) => PrivateFiles.WriteText(path, key.Line + "\n");

    /// <summary>
    /// The confirmed key, from <paramref name="path"/>; for an install set up before OpenSSH (a fingerprint in the
    /// settings, no known_hosts yet), the server's key is recorded now and kept only if it is the confirmed one.
    /// </summary>
    /// <exception cref="HostKeyChangedException">the server offers another key than the one confirmed at setup</exception>
    /// <exception cref="StageException">no key was ever confirmed, or the server can't be reached</exception>
    public static async Task<HostKey> EnsureAsync(SshTools tools, ServerProfile profile, string path, CancellationToken cancel)
    {
        var confirmed = profile.HostKey
                        ?? throw new StageException(Stage.Server, $"The host key of {profile.Display} was never confirmed; run the server setup again.");
        if (Read(path).FirstOrDefault(k => ServerProfile.SameKey(k.Fingerprint, confirmed)) is { } known)
            return known;
        var offered = await RecordAsync(tools, profile, cancel);
        if (!ServerProfile.SameKey(offered.Fingerprint, confirmed))
            throw new HostKeyChangedException(profile.Display, confirmed, offered.Fingerprint);
        Save(path, offered);
        return offered;
    }
}

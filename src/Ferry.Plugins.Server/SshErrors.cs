using Ferry.Core;
namespace Ferry.Plugins.Server;

/// <summary>What went wrong in a run of ssh that ended with its own exit code, 255.</summary>
internal enum SshFailureKind
{
    /// <summary>The server's key isn't the one in known_hosts: the connection may be intercepted.</summary>
    HostKeyChanged,

    /// <summary>known_hosts has no key for the server (a known_hosts lost or not written yet).</summary>
    HostKeyUnknown,

    /// <summary>The server refused the login.</summary>
    LoginRefused,

    /// <summary>ssh can't use the key file (permissions, format, missing).</summary>
    KeyFile,

    /// <summary>The server can't be reached, or the connection dropped: trying again may work.</summary>
    Unreachable,

    Other,
}

/// <param name="Detail">the line of stderr that says what happened, for the message</param>
internal sealed record SshFailure(SshFailureKind Kind, string Detail);

/// <summary>
/// ssh's failures from what it printed (docs/openssh-plan.md §3.6). The wording is OpenSSH's and has been stable for
/// years; the tests hold samples from Windows' 9.5p2 and Linux 9.x and 10.x.
/// </summary>
internal static class SshErrors
{
    private const string RefusedLogin = " refused the login: ";
    private const string CommandFailedStart = "A command failed on ";

    /// <summary>The exit code ssh ends with for a failure of its own; any other is the remote command's.</summary>
    public const int SshFailed = 255;

    private const int MaxDetailChars = 300;

    // first match wins: a changed key prints "Host key verification failed" too, and a refused key "Permission denied"
    private static readonly (string Text, SshFailureKind Kind)[] Table =
    [
        ("REMOTE HOST IDENTIFICATION HAS CHANGED", SshFailureKind.HostKeyChanged),
        ("host key for", SshFailureKind.HostKeyChanged),                     // "...but the host key for X has changed"
        ("host key is known for", SshFailureKind.HostKeyUnknown),            // "No ED25519 host key is known for X and you have requested strict checking."
        ("Host key verification failed", SshFailureKind.HostKeyChanged),
        ("UNPROTECTED PRIVATE KEY FILE", SshFailureKind.KeyFile),
        ("bad permissions", SshFailureKind.KeyFile),
        ("Load key", SshFailureKind.KeyFile),                                // "Load key \"...\": invalid format"
        ("no such identity", SshFailureKind.KeyFile),
        ("Permission denied", SshFailureKind.LoginRefused),
        ("Too many authentication failures", SshFailureKind.LoginRefused),
        ("Could not resolve hostname", SshFailureKind.Unreachable),
        ("Connection refused", SshFailureKind.Unreachable),
        ("Connection timed out", SshFailureKind.Unreachable),
        ("Operation timed out", SshFailureKind.Unreachable),
        ("Network is unreachable", SshFailureKind.Unreachable),
        ("No route to host", SshFailureKind.Unreachable),
        ("Connection closed by", SshFailureKind.Unreachable),
        ("Connection reset", SshFailureKind.Unreachable),
        ("Broken pipe", SshFailureKind.Unreachable),
        ("not responding", SshFailureKind.Unreachable),                      // "Timeout, server X not responding."
        ("kex_exchange_identification", SshFailureKind.Unreachable),
        ("connection abort", SshFailureKind.Unreachable),                    // Windows: "Software caused connection abort"
        ("client_loop: send disconnect", SshFailureKind.Unreachable),
    ];

    /// <summary>What <paramref name="errors"/> (ssh's stderr) says went wrong.</summary>
    public static SshFailure Classify(string errors)
    {
        foreach (var (text, kind) in Table)
        {
            if (!errors.Contains(text, StringComparison.OrdinalIgnoreCase))
                continue;
            var detail = Detail(errors, text);
            // a refused login says what else ssh printed (LogLevel=ERROR keeps that short): why a password or
            // passphrase never arrived, from askpass or from the program that should have answered
            if (kind is SshFailureKind.LoginRefused or SshFailureKind.KeyFile && Others(errors, text) is { Length: > 0 } others)
                detail = $"{detail} ({others})";
            return new SshFailure(kind, detail);
        }
        return new SshFailure(SshFailureKind.Other, Detail(errors, null));
    }

    /// <summary>The exception for a failure of ssh's own, as the rest of server mode expects it.</summary>
    /// <param name="display">"user@host", for the message</param>
    /// <param name="expectedHostKey">the fingerprint confirmed at setup</param>
    /// <param name="offeredHostKey">the fingerprint the server offered, when it was recorded</param>
    public static Exception ToException(SshFailure failure, string display, string? expectedHostKey, string? offeredHostKey = null) =>
        failure.Kind switch
        {
            SshFailureKind.HostKeyChanged => new HostKeyChangedException(display, expectedHostKey ?? "?", offeredHostKey ?? "?"),
            SshFailureKind.HostKeyUnknown => new StageException(Stage.Server,
                $"Ferry has no host key for {display}; run the server setup again to confirm it."),
            SshFailureKind.LoginRefused => new StageException(Stage.Server, $"{display}{RefusedLogin}{failure.Detail}"),
            SshFailureKind.KeyFile => new StageException(Stage.Server, $"ssh can't use the key for {display}: {failure.Detail}"),
            SshFailureKind.Unreachable => new StageException(Stage.Server, $"Could not reach {display}: {failure.Detail}", isRetryable: true),
            _ => new StageException(Stage.Server, $"ssh to {display} failed: {failure.Detail}", isRetryable: true),
        };

    /// <summary>Whether <paramref name="e"/> is the server refusing a login, not something going wrong after one.</summary>
    public static bool IsLoginRefused(Exception e) => e is StageException && e.Message.Contains(RefusedLogin, StringComparison.Ordinal);

    /// <summary>Whether <paramref name="e"/> is a command that ran on the server and failed there.</summary>
    public static bool IsCommandFailure(Exception e) => e is StageException && e.Message.StartsWith(CommandFailedStart, StringComparison.Ordinal);

    /// <summary>The remote command itself failed (any exit code but 255), as ServerConnection.RunAsync has always said it.</summary>
    public static StageException CommandFailed(string display, int exitCode, string errors) =>
        new(Stage.Server, $"{CommandFailedStart}{display}: {errors.Trim()} (exit {exitCode}).");

    /// <summary>The stderr lines other than the one that matched, askpass's first, as one line, not too long.</summary>
    private static string Others(string errors, string matched)
    {
        var lines = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Where(l => !l.StartsWith("@@@", StringComparison.Ordinal) && !l.Contains(matched, StringComparison.OrdinalIgnoreCase))
                          .Select(l => new string([.. l.Where(c => !char.IsControl(c))]).Trim())
                          .Where(l => l.Length > 0)
                          .Distinct()
                          .OrderByDescending(l => l.Contains("askpass", StringComparison.OrdinalIgnoreCase))
                          .ToList();
        var text = string.Join(" / ", lines);
        return text.Length > MaxDetailChars ? text[..MaxDetailChars] + "…" : text;
    }

    /// <summary>The stderr line that matched (else the last one), one line, no control characters, not too long.</summary>
    private static string Detail(string errors, string? matched)
    {
        var lines = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Where(l => !l.StartsWith("@@@", StringComparison.Ordinal))
                          .ToList();
        var line = (matched is null ? null : lines.FirstOrDefault(l => l.Contains(matched, StringComparison.OrdinalIgnoreCase)))
                   ?? lines.LastOrDefault()
                   ?? "it ended without saying why";
        var clean = new string([.. line.Where(c => !char.IsControl(c))]).Trim();
        return clean.Length > MaxDetailChars ? clean[..MaxDetailChars] + "…" : clean;
    }
}

using System.Globalization;
using System.Text;

namespace Ferry.Plugins.Server;

/// <summary>How ssh logs in.</summary>
public enum SshLogin
{
    /// <summary>Ferry's key (or the user's), never asking anything: every connection after setup.</summary>
    Key,

    /// <summary>A password, answered through askpass: only setup's first login, which installs the key.</summary>
    Password,

    /// <summary>No login at all: connect only to record the server's host key into a scratch known_hosts. It ends
    /// with "Permission denied", which is expected.</summary>
    RecordHostKey,
}

/// <summary>Where to log in, and the files ssh checks it against.</summary>
/// <param name="KeyFile">the private key; unused for <see cref="SshLogin.Password"/> and <see cref="SshLogin.RecordHostKey"/></param>
/// <param name="KnownHostsFile">Ferry's own known_hosts, never the user's; for <see cref="SshLogin.RecordHostKey"/>
/// a scratch file the key is recorded into</param>
/// <param name="HostKeyType">the confirmed host key's type ("ssh-ed25519"), so ssh checks exactly that key; null
/// when recording</param>
/// <param name="Keepalive">how often an idle connection is checked; three unanswered checks end it</param>
public sealed record SshTarget(string Host, int Port, string User, string KeyFile, string KnownHostsFile, string? HostKeyType,
                               TimeSpan Keepalive);

/// <summary>
/// The arguments of every ssh run, built in one place so every run behaves the same (docs/openssh-plan.md §3.1): the
/// user's ~/.ssh/config and known_hosts are never read, nothing is ever asked on a terminal, a dead connection ends the
/// process, and the remote command comes last, after "--".
/// </summary>
internal static class SshArguments
{
    /// <summary>AES-GCM first: with AES-NI on both ends it is the fastest; CTR for servers without GCM.</summary>
    public const string Ciphers = "aes128-gcm@openssh.com,aes256-gcm@openssh.com,chacha20-poly1305@openssh.com,aes128-ctr,aes256-ctr";

    private const int ConnectTimeoutSeconds = 15;
    private const int KeepaliveCountMax = 3;

    /// <param name="command">the remote command, already quoted for the server's shell (<see cref="AgentInstaller.Quote"/>)</param>
    /// <param name="askpass">a key login whose passphrase askpass answers: BatchMode would stop ssh asking for it</param>
    public static List<string> For(SshTarget target, SshLogin login, string command, bool askpass = false)
    {
        var arguments = new List<string> { "-F", "none" };
        void Option(string name, string value) => arguments.AddRange(["-o", $"{name}={value}"]);

        arguments.AddRange(["-p", target.Port.ToString(CultureInfo.InvariantCulture), "-l", target.User]);
        Option("UserKnownHostsFile", ConfigPath(target.KnownHostsFile));
        Option("GlobalKnownHostsFile", ConfigPath(target.KnownHostsFile));
        Option("UpdateHostKeys", "no");
        Option("HashKnownHosts", "no");
        Option("CheckHostIP", "no");
        switch (login)
        {
            case SshLogin.Key:
                arguments.AddRange(["-i", FilePath(target.KeyFile)]);
                Option("IdentitiesOnly", "yes");
                Option("IdentityAgent", "none");
                Option("PreferredAuthentications", "publickey");
                Option("StrictHostKeyChecking", "yes");
                Option("BatchMode", askpass ? "no" : "yes"); // no terminal either way: only askpass can be asked
                if (askpass)
                    Option("NumberOfPasswordPrompts", "1");
                break;
            case SshLogin.Password:
                Option("PubkeyAuthentication", "no");
                Option("IdentityAgent", "none");
                Option("PreferredAuthentications", "password,keyboard-interactive");
                Option("NumberOfPasswordPrompts", "1");
                Option("StrictHostKeyChecking", "yes");
                Option("BatchMode", "no"); // askpass answers; there is no terminal to ask on
                break;
            case SshLogin.RecordHostKey:
                foreach (var method in new[] { "PubkeyAuthentication", "PasswordAuthentication", "KbdInteractiveAuthentication",
                                               "GSSAPIAuthentication", "HostbasedAuthentication" })
                    Option(method, "no");
                Option("IdentityAgent", "none");
                Option("StrictHostKeyChecking", "accept-new");
                Option("BatchMode", "yes");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(login), login, null);
        }
        if (login != SshLogin.RecordHostKey && target.HostKeyType is { } type)
            Option("HostKeyAlgorithms", HostKeyAlgorithmsFor(type));
        Option("ConnectTimeout", ConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        Option("ConnectionAttempts", "1");
        Option("ServerAliveInterval", Math.Max(1, (int)target.Keepalive.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        Option("ServerAliveCountMax", KeepaliveCountMax.ToString(CultureInfo.InvariantCulture));
        Option("TCPKeepAlive", "yes");
        Option("Ciphers", Ciphers);
        Option("Compression", "no");
        Option("IPQoS", "throughput");
        Option("ControlMaster", "no");
        Option("ClearAllForwardings", "yes");
        Option("PermitLocalCommand", "no");
        Option("LogLevel", "ERROR");
        // no tty, no X11, no agent forwarding, and no escape character: stdin may be a binary file (an upload)
        arguments.AddRange(["-T", "-x", "-a", "-e", "none"]);
        arguments.AddRange([target.Host, "--", command]);
        return arguments;
    }

    /// <summary>The algorithms that check a host key of <paramref name="keyType"/>: an RSA key signs with SHA-2.</summary>
    internal static string HostKeyAlgorithmsFor(string keyType) =>
        keyType == "ssh-rsa" ? "rsa-sha2-512,rsa-sha2-256" : keyType;

    /// <summary>
    /// A path for an option ssh reads as a config value: split on spaces unless quoted, backslashes taken as escapes
    /// before quotes, and "%x" expanded. Forward slashes (Win32-OpenSSH takes them), "%" doubled, quoted when needed.
    /// </summary>
    internal static string ConfigPath(string path)
    {
        var value = FilePath(path);
        if (!value.Any(c => char.IsWhiteSpace(c) || c is '"' or '\''))
            return value;
        var quoted = new StringBuilder("\"");
        foreach (var c in value)
            quoted.Append(c is '"' or '\\' ? "\\" + c : c.ToString());
        return quoted.Append('"').ToString();
    }

    /// <summary>A path for -i, which ssh expands "%x" and "~" in: forward slashes on Windows, "%" doubled.</summary>
    /// <exception cref="ArgumentException">the path has "${", which ssh would read as an environment variable</exception>
    internal static string FilePath(string path)
    {
        if (path.Contains("${", StringComparison.Ordinal))
            throw new ArgumentException($"ssh can't be given a path with \"${{\" in it: {path}", nameof(path));
        var value = OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;
        return value.Replace("%", "%%", StringComparison.Ordinal);
    }
}

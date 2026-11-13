using Ferry.Core.Settings;
using Ferry.Core.Text;

namespace Ferry.Plugins.Server;

/// <summary>Where files are downloaded.</summary>
public enum DownloadMode
{
    /// <summary>On this PC.</summary>
    Local,

    /// <summary>On the user's server, then brought over by SSH.</summary>
    Server,
}

/// <summary>What Server Support adds to Ferry: its settings, and how to read them.</summary>
public static class ServerKeys
{
    /// <summary>Whether Server Support is in use; off, everything downloads on this PC, whatever else is set.</summary>
    public static readonly SettingKey Enabled = new("FERRY_PLUGIN_SERVER", SettingType.Boolean, "false",
        "Downloading on your own Linux server, also while this PC is off.")
    { Title = "Server Support" };

    public static readonly SettingKey Mode = new("FERRY_MODE", SettingType.Choice, "local",
        "Where files are downloaded: local (this PC) or server (your server, then brought over by SSH).")
    {
        Title = "Where files are downloaded",
        Choices = ["local", "server"],
    };

    public static readonly SettingKey ServerHost = new("FERRY_SERVER_HOST", SettingType.Text, "",
        "Server mode: the server's host name or address.")
    { Title = "Server" };

    public static readonly SettingKey ServerPort = new("FERRY_SERVER_PORT", SettingType.WholeNumber, "22",
        "Server mode: the server's SSH port.")
    {
        Title = "SSH port",
        Min = 1,
        Max = 65535,
    };

    public static readonly SettingKey ServerUser = new("FERRY_SERVER_USER", SettingType.Text, "",
        "Server mode: the user to log in as.")
    { Title = "User" };

    public static readonly SettingKey ServerHostKey = new("FERRY_SERVER_HOST_KEY", SettingType.Text, "",
        "Server mode: the server's key fingerprint, confirmed at setup.")
    { Title = "Host key" };

    public static readonly SettingKey ServerKey = new("FERRY_SERVER_KEY", SettingType.Text, "",
        "Server mode: the private key file Ferry logs in with, set at setup (Ferry's own, or one you chose).")
    { Title = "Login key" };

    public static readonly SettingKey ServerFolder = new("FERRY_SERVER_FOLDER", SettingType.Text, "~/.ferry/downloads",
        "Server mode: where the server keeps files until they are brought over.")
    { Title = "Folder on the server" };

    public static readonly SettingKey ServerKeepFiles = new("FERRY_SERVER_KEEP_FILES", SettingType.Boolean, "false",
        "Server mode: keep files on the server after they were brought over and checked.")
    { Title = "Keep files on the server" };

    public static readonly SettingKey TransferLimit = new("FERRY_TRANSFER_LIMIT", SettingType.Size, "0",
        "Server mode: speed limit per second for bringing files over, such as 10M; 0 for none.")
    {
        Title = "Speed limit for bringing files here",
        Min = 0,
        Max = 100 * Units.GiB,
    };

    public static readonly SettingKey TransfersAtOnce = new("FERRY_TRANSFERS_AT_ONCE", SettingType.WholeNumber, "2",
        "Server mode: files brought over at the same time.")
    {
        Title = "Files brought over at once",
        Min = 1,
        Max = 16,
    };

    public static readonly SettingKey ServerAgent = new("FERRY_SERVER_AGENT", SettingType.Text, "",
        "Server mode: the agent build to upload (ferry for Linux); found next to this program when not set.")
    { Title = "Agent build to upload" };

    public static readonly SettingKey ServerKeepalive = new("FERRY_SERVER_KEEPALIVE", SettingType.Duration, "30s",
        "Server mode: how often the connection to the server is checked while idle.")
    {
        Title = "Server keepalive",
        MinDuration = TimeSpan.FromSeconds(5),
        MaxDuration = TimeSpan.FromMinutes(10),
    };

    public static readonly SettingKey SshPath = new("FERRY_SSH_PATH", SettingType.Text, "",
        "Server mode: the OpenSSH client to use, its folder or ssh itself (8.4 or newer); found by itself when not set.")
    { Title = "OpenSSH client" };

    /// <summary>What the plugin's page lists as advanced; the rest is set by the setup, or has a card of its own.</summary>
    public static IReadOnlyList<SettingKey> Advanced { get; } = [ServerFolder, ServerPort, TransfersAtOnce, ServerKeepalive, ServerAgent, SshPath];

    /// <summary>Puts these settings in the one table. Once, as the process starts, before any settings are read.</summary>
    public static void Register() => SettingKey.Contribute(
    [
        Enabled, Mode, ServerHost, ServerUser, ServerKeepFiles, TransferLimit, ServerPort, ServerHostKey, ServerKey, ServerFolder,
        TransfersAtOnce, ServerAgent, ServerKeepalive, SshPath,
    ]);

    extension(FerrySettings settings)
    {
        public bool ServerSupportIsOn => settings.Boolean(ServerKeys.Enabled);

        /// <summary>Where files are downloaded; on this PC while Server Support is off.</summary>
        public DownloadMode Mode =>
            settings.ServerSupportIsOn ? Enum.Parse<DownloadMode>(settings.Text(ServerKeys.Mode), ignoreCase: true) : DownloadMode.Local;

        /// <summary>Server mode: keep files on the server after they were brought over.</summary>
        public bool KeepServerFiles => settings.Boolean(ServerKeys.ServerKeepFiles);

        /// <summary>Server mode: bytes per second for bringing files over, or none.</summary>
        public long TransferLimit => settings.Size(ServerKeys.TransferLimit);

        public int TransfersAtOnce => settings.Number(ServerKeys.TransfersAtOnce);
    }
}

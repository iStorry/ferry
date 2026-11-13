using Ferry.Core;
using Ferry.Core.AppLocking;
using Ferry.Core.Files;
using Ferry.Core.Queue;
using Ferry.Core.Settings;
using Ferry.Plugins.Server;
using Microsoft.UI.Dispatching;

namespace Ferry.App.Services;

/// <summary>What every page shares: where files live, the settings, the login secrets, and the queue.</summary>
public sealed class AppServices
{
    private const string WelcomeDone = "welcomeDone";

    // the decoy's own folder, removed when it ends; null for the real session
    private readonly string? _decoyFolder;

    /// <summary>The real session, over the user's own files. Nothing is read but the settings until it is started.</summary>
    public AppServices(DispatcherQueue ui)
        : this(ui, AppPaths.ForApp(), environment: null)
    {
        CrashLog.Folder = ConfigFolder;
    }

    private AppServices(DispatcherQueue ui, AppPaths paths, IReadOnlyDictionary<string, string>? environment, string? decoyFolder = null)
    {
        Paths = paths;
        Store = new SettingsStore(Paths, environment);
        _decoyFolder = decoyFolder;
        // beside the settings file, so FERRY_CONFIG_PATH moves all of it (a portable copy, a test run)
        ConfigFolder = Path.GetDirectoryName(Store.FilePath)!;
        QueueFile = Path.Combine(ConfigFolder, "queue.json");
        Secrets = new SecretStore(Path.Combine(ConfigFolder, "secrets"));
        Lock = new AppLock(Secrets);
        // before the settings are read: an installed plugin's are among them
        Installed = new InstalledPlugins(new Ferry.Core.Plugins.PluginStore(Path.Combine(ConfigFolder, "plugins")));
        Installed.Load();
        Settings = Store.Load();
        Queue = new QueueHost(this, ui);
        Speed = new QueueSpeed(Queue, ui);
        Updater = new Updater(this, ui);
        Browse = new BrowseHost(this, ui);
        PluginUpdates = new PluginUpdates(this);
    }

    /// <summary>
    /// A session as a new install has it, for too many wrong PINs: its own empty folder (and no environment, which
    /// could point it at the real settings), the Welcome done, and only the theme taken over, so the window doesn't
    /// change as it opens. Nothing of the real session is read or written.
    /// </summary>
    /// <exception cref="IOException">its folder can't be made</exception>
    /// <exception cref="SettingsException">its settings can't be written</exception>
    public static AppServices Decoy(DispatcherQueue ui, AppServices real)
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        var paths = new AppPaths(Path.Combine(folder, "config"), Path.Combine(folder, Product.Name));
        var decoy = new AppServices(ui, paths, new Dictionary<string, string>(), folder);
        decoy.FinishWelcome();
        if (real.Settings[SettingKey.Theme].Source != SettingSource.Default)
            decoy.Change(store => store.Set(SettingKey.Theme.Name, real.Settings.Text(SettingKey.Theme)));
        return decoy;
    }

    /// <summary>Opened for too many wrong PINs: Downloads and About only, over a folder of its own.</summary>
    public bool IsDecoy => _decoyFolder is not null;

    public AppPaths Paths { get; }

    /// <summary>Where the settings, the queue and the secrets are.</summary>
    public string ConfigFolder { get; }

    /// <summary>The PIN asked for at start; a decoy's is off.</summary>
    public AppLock Lock { get; }

    public SettingsStore Store { get; }

    public SecretStore Secrets { get; }

    /// <summary>The local queue, kept so unfinished downloads come back after a restart.</summary>
    public string QueueFile { get; }

    /// <summary>The settings in effect; replaced (and <see cref="SettingsChanged"/> raised) after every change.</summary>
    public FerrySettings Settings { get; private set; }

    public QueueHost Queue { get; }

    /// <summary>The queue's speed over the last minute, for the Downloads page's graph.</summary>
    public QueueSpeed Speed { get; }

    public Updater Updater { get; }

    /// <summary>The page reader and album watcher, for the Browser page, while an installed plugin has a site to browse.</summary>
    public BrowseHost Browse { get; }

    /// <summary>Which of the browsed site's files were downloaded here, or null when that can't be kept.</summary>
    public Ferry.Core.Browse.DownloadedFiles? Downloaded { get; private set; }

    /// <summary>The installed plugin that looks titles up for the Library page: the first that does and is on. Null when there is none, and for a decoy.</summary>
    public InstalledPlugin? LibraryPlugin =>
        IsDecoy ? null : Installed.All.FirstOrDefault(installed => installed.Stored.Manifest.LooksUpTitles && installed.Plugin.IsOn(Settings));

    /// <summary>Newer packages of the installed plugins, where they are looked for.</summary>
    public PluginUpdates PluginUpdates { get; }

    /// <summary>The plugins someone installed, beside those that come with Ferry.</summary>
    public InstalledPlugins Installed { get; }

    /// <summary>Raised on the UI thread after a setting changed.</summary>
    public event Action? SettingsChanged;

    /// <summary>Read the settings again (after something else changed the file, such as the server setup).</summary>
    public void Reload()
    {
        Settings = Store.Load();
        SettingsChanged?.Invoke();
    }

    /// <summary>The first run.</summary>
    public bool ShowWelcome => !Settings.State.ContainsKey(WelcomeDone);

    /// <summary>Change settings through the store, then tell everyone.</summary>
    /// <exception cref="SettingsException">the value can't be used; nothing changed</exception>
    public FerrySettings Change(Func<SettingsStore, FerrySettings> change)
    {
        Settings = change(Store);
        SettingsChanged?.Invoke();
        return Settings;
    }

    /// <summary>Browsing follows the queue's connection and the plugins, from when the session opens.</summary>
    public void StartBrowsing()
    {
        TrackDownloaded();
        Browse.Start();
    }

    /// <summary>A file of the browsed site that the queue finished, or found already there, is one downloaded here, also once it leaves the queue.</summary>
    private void TrackDownloaded()
    {
        try
        {
            Downloaded = new Ferry.Core.Browse.DownloadedFiles(Path.Combine(ConfigFolder, "downloaded"), Secrets, CrashLog.Write);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            CrashLog.Write("opening the downloaded files", e);
            return;
        }
        Queue.Changed += e => NoteDownloaded(e.Item);
        Queue.QueueReplaced += () =>
        {
            foreach (var item in Queue.Queue?.Items ?? [])
                NoteDownloaded(item);
        };
    }

    private void NoteDownloaded(QueueItem item)
    {
        if (item.State is QueueState.Done or QueueState.Skipped && Browse.IsOwn(item.Link.SiteId))
            Downloaded?.Add(item.Link.Key);
    }

    /// <summary>The window closed: a decoy's folder, and all it downloaded, goes with it.</summary>
    public void EndDecoy()
    {
        if (_decoyFolder is null)
            return;
        try
        {
            Directory.Delete(_decoyFolder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write("removing the decoy's folder", e); // in the user's temp folder, which Windows clears in time
        }
    }

    public void FinishWelcome() => Change(store => store.SetState(WelcomeDone, "true"));

    /// <summary>Something the app remembers that isn't a setting (window placement).</summary>
    public string? State(string name) => Settings.State.TryGetValue(name, out var value) ? value : null;

    public void SetState(string name, string? value)
    {
        try
        {
            Settings = Store.SetState(name, value);
        }
        catch (SettingsException)
        {
            // only remembered for next time; an unreadable settings file is reported on the Settings page
        }
    }
}

using System.Text.RegularExpressions;
using Ferry.App.Services;
using Ferry.Plugins.BrowserLink;
using Ferry.Plugins.Official;
using Ferry.Plugins.Server;
using Ferry.Plugins.Torrents;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Ferry.App;

public partial class App : Application
{
    // what second launches brought before the window was there to take it; they are heard on another thread
    private static readonly Lock EarlyLaunchesLock = new();
    private static readonly List<string> EarlyLaunches = [];

    public App()
    {
        OfficialSites.Register(TorrentsPlugin.Plugin);
        ServerKeys.Register();
        InitializeComponent();
        // a bug in one handler shouldn't take the window (and every download) with it: it is logged and the app goes on
        UnhandledException += (_, e) =>
        {
            CrashLog.Write("UI thread", e.Exception);
            e.Handled = Window is not null; // before the window is up there is nothing to go on with
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write("background thread", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("unobserved task", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>What the pages share; made before the window.</summary>
    public static AppServices Services { get; private set; } = null!;

    /// <summary>The one window.</summary>
    public static MainWindow? Window { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Services = new AppServices(DispatcherQueue.GetForCurrentThread());
            Accent.Apply(Services.Settings); // before anything is drawn in it
            Window = new MainWindow();
            Window.Closed += (_, _) => Services.EndDecoy();
            Window.Activate(); // the session opens once the window is on screen
        }
#pragma warning disable CA1031 // no window: log why and end, or this process would keep the single-instance key and every later launch would go to it
        catch (Exception e)
#pragma warning restore CA1031
        {
            CrashLog.Write("starting", e);
            Window = null;
            Exit();
            return;
        }
        GiveLinks(string.Join(' ', Environment.GetCommandLineArgs().Skip(1)));
        foreach (var text in TakeEarlyLaunches())
            GiveLinks(text);
    }

    private static List<string> TakeEarlyLaunches()
    {
        lock (EarlyLaunchesLock)
        {
            var taken = new List<string>(EarlyLaunches);
            EarlyLaunches.Clear();
            return taken;
        }
    }

    /// <summary>What a command line brings: a link the browser handed over, with its login, or web links.</summary>
    private static void GiveLinks(string text)
    {
        if (FromBrowser().Match(text) is { Success: true } handed && BrowserLinkPlugin.TakeHandoff(handed.Groups["id"].Value) is { } handoff)
            Window?.AddLinks([handoff.Link], handoff.Details);
        else
            Window?.AddLinks(Links(text));
    }

    /// <summary>Too many wrong PINs: the pages made from now on get an empty session instead of the real one.</summary>
    /// <exception cref="IOException">the decoy's folder can't be made</exception>
    /// <exception cref="Core.Settings.SettingsException">the decoy's settings can't be written</exception>
    public static void UseDecoy() => Services = AppServices.Decoy(DispatcherQueue.GetForCurrentThread(), Services);

    /// <summary>A second launch handed its activation over: take its links and come to the front.</summary>
    public static void OnRedirected(AppActivationArguments args)
    {
        var text = args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch ? launch.Arguments : "";
        MainWindow? window;
        lock (EarlyLaunchesLock)
        {
            window = Window;
            if (window is null)
            {
                EarlyLaunches.Add(text);
                return;
            }
        }
        window.DispatcherQueue.TryEnqueue(() =>
        {
            window.BringToFront();
            GiveLinks(text);
        });
    }

    /// <summary>The web links in a command line.</summary>
    private static List<string> Links(string text) =>
        [.. LinkPattern().Matches(text).Select(m => m.Value.TrimEnd('"', '\''))];

    [GeneratedRegex(@"https?://[^\s""']+")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"--from-browser\s+(?<id>[0-9a-f]{32})\b")]
    private static partial Regex FromBrowser();
}

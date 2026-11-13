using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Ferry.App;

/// <summary>
/// The app's own Main (DISABLE_XAML_GENERATED_MAIN): one instance per user. A second launch, such as
/// "Ferry.exe &lt;link&gt;", hands its activation (and links) to the running one and exits.
/// </summary>
public static class Program
{
    /// <summary>Held while Ferry runs; the installer checks it (AppMutex) before replacing files.</summary>
    private const string MutexName = "Ferry";

    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        using var running = new Mutex(false, MutexName, out var first);

        var instance = AppInstance.FindOrRegisterForKey(MutexName);
        if (!instance.IsCurrent)
        {
            // off the STA thread: redirecting waits on COM, which this thread would otherwise have to pump
            Task.Run(() => instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs()).AsTask()).Wait();
            return 0;
        }
        if (!first)
        {
            // Another Ferry runs from another folder (an unpackaged app's instances are told apart by their path, so
            // the redirect above can't reach it).
            OtherCopy.Tell();
            return 0;
        }
        instance.Activated += (_, activation) => App.OnRedirected(activation);

        Application.Start(started =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }
}

using Microsoft.UI.Xaml;

namespace Ferry.App.Services;

/// <summary>What the Try now button does wherever a page says the server can't be reached.</summary>
public static class ServerRetry
{
    /// <summary>Reach the server now, or with none set up, go and set one up. What goes wrong is said.</summary>
    public static Task RunAsync(XamlRoot? root, AppServices app)
    {
        if (app.Queue.Status != ModeStatus.NotSetUp)
            return Dialogs.GuardAsync(root, "Couldn't reach the server", app.Queue.RetryNowAsync);
        App.Window?.ShowServerSetup();
        return Task.CompletedTask;
    }
}

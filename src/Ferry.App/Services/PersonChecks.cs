using System.Runtime.InteropServices;
using Ferry.Core;
using Ferry.Core.Http;
using Ferry.Plugins.Server;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Ferry.App.Services;

/// <summary>
/// A site's check that only a person can pass (<see cref="PersonCheck"/>), shown to the person: the site's own page in a
/// browser window, reached through the server so the site sees the address its pages are then read from. Ferry types
/// nothing and answers nothing. When the site has set its cookie, the window closes and the cookie goes to the server.
/// One window at a time: whoever else waited for the same check gets the same answer.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "It lives as long as the session, as the hosts beside it do.")]
public sealed class PersonChecks(AppServices app, DispatcherQueue ui)
{
    // a check given up on isn't shown again for this long: an album's pictures would each ask
    private static readonly TimeSpan LeaveAlone = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    // the last answer per site and cookie, with when it came; used inside the gate only
    private readonly Dictionary<string, (PassedCheck? Pass, long At)> _last = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What passing <paramref name="check"/> earned, or null when the person closed the window, or it can't be shown.</summary>
    /// <exception cref="StageException">the server can't be reached for it, or doesn't pass connections on (<see cref="Stage.Server"/>)</exception>
    public async Task<PassedCheck?> PassAsync(PersonCheck check, CancellationToken cancel)
    {
        if (app.IsDecoy || !check.IsUsable)
            return null;
        var asked = Environment.TickCount64;
        await _oneAtATime.WaitAsync(cancel);
        try
        {
            var key = $"{check.Page.Host}\n{check.Cookie}";
            if (_last.TryGetValue(key, out var last)
                && (last.At >= asked || (last.Pass is null && Environment.TickCount64 - last.At < LeaveAlone.TotalMilliseconds)))
                return last.Pass;
            PassedCheck? pass = null;
            try
            {
                pass = await ShowAsync(check, cancel);
            }
            finally
            {
                _last[key] = (pass, Environment.TickCount64);
            }
            return pass;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task<PassedCheck?> ShowAsync(PersonCheck check, CancellationToken cancel)
    {
        await using var connection = await ServerConnection.OpenAsync(app.Settings, app.Secrets, cancel);
        await using var tunnel = await connection.OpenTunnelAsync(check.Page.Host, cancel);
        var where = app.Queue.ServerDisplay ?? "your server";
        return await OnUiAsync(() => CheckWindow.ShowAsync(check, tunnel.Port, where, cancel));
    }

    private Task<T> OnUiAsync<T>(Func<Task<T>> work)
    {
        if (ui.HasThreadAccess)
            return work();
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!ui.TryEnqueue(async () =>
            {
                try
                {
                    done.TrySetResult(await work());
                }
                catch (OperationCanceledException e)
                {
                    done.TrySetCanceled(e.CancellationToken);
                }
#pragma warning disable CA1031 // whatever it is goes to whoever asked, not to the dispatcher
                catch (Exception e)
#pragma warning restore CA1031
                {
                    done.TrySetException(e);
                }
            }))
            done.TrySetException(new StageException(Stage.Server, "Ferry is closing, so the check can't be shown."));
        return done.Task;
    }
}

/// <summary>The window a check is shown in: the site's page and a line saying what it is. It has its own browser profile, removed after.</summary>
internal static class CheckWindow
{
    public static async Task<PassedCheck?> ShowAsync(PersonCheck check, int proxyPort, string where, CancellationToken cancel)
    {
        var profile = Directory.CreateTempSubdirectory("ferry_check_").FullName;
        var done = new TaskCompletionSource<PassedCheck?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var view = new WebView2();
        var note = new TextBlock
        {
            Text = $"{check.Page.Host} wants to know a person is there. This is its own page, opened through {where}. " +
                   "Ferry carries on by itself once you have passed; close the window to give up.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 12, 16, 12),
        };
        var layout = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() } };
        layout.Children.Add(note);
        Grid.SetRow(view, 1);
        layout.Children.Add(view);
        var window = new Window { Title = $"{check.Page.Host}: a check for a person", Content = layout };
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 680));
        var closed = false;
        window.Closed += (_, _) =>
        {
            closed = true;
            done.TrySetResult(null);
        };
        await using var stop = cancel.Register(() => done.TrySetCanceled(cancel));
        try
        {
            // socks5, not socks4: the name is looked up by the server, as it will be when the pages are read there
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"--proxy-server=socks5://127.0.0.1:{proxyPort}"),
            };
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync("", profile, options);
            window.Activate();
            await view.EnsureCoreWebView2Async(environment);
            var core = view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            // the window is for this one site's check: nothing else opens in it
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.NavigationStarting += (_, e) =>
                e.Cancel = !Uri.TryCreate(e.Uri, UriKind.Absolute, out var to) || !HttpFetcher.IsSameSite(to, check.Page);
            core.NavigationCompleted += async (_, _) =>
            {
                try
                {
                    // the site's page reloads itself once it has set its cookie: that is how a pass is seen
                    var cookies = await core.CookieManager.GetCookiesAsync(check.Page.AbsoluteUri);
                    if (cookies.FirstOrDefault(cookie => cookie.Name == check.Cookie) is { Value.Length: > 0 } cookie)
                        done.TrySetResult(new PassedCheck(check.Page, check.Cookie, cookie.Value, core.Settings.UserAgent));
                }
                catch (Exception e) when (e is COMException or InvalidOperationException or ObjectDisposedException)
                {
                    // the window closed meanwhile
                }
            };
            core.Navigate(check.Page.AbsoluteUri);
            return await done.Task;
        }
        catch (Exception e) when (e is COMException or FileNotFoundException or InvalidOperationException)
        {
            throw new StageException(Stage.Server,
                $"The check of {check.Page.Host} couldn't be shown: {e.Message} It needs Microsoft Edge WebView2, which comes with Windows 11.", inner: e);
        }
        finally
        {
            view.Close();
            if (!closed)
                window.Close();
            _ = RemoveAsync(profile);
        }
    }

    /// <summary>The browser lets go of its profile a moment after its window: tried a few times, then left to Windows' own clearing of temp.</summary>
    private static async Task RemoveAsync(string profile)
    {
        for (var tries = 0; tries < 5; tries++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            try
            {
                Directory.Delete(profile, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // still held
            }
        }
    }
}

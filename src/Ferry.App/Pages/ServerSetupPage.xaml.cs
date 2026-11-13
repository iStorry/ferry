using Ferry.App.Services;
using Ferry.App.ViewModels;
using Ferry.Core;
using Ferry.Core.Settings;
using Ferry.Core.Text;
using Ferry.Plugins.Server;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Ferry.App.Pages;

/// <summary>The server setup: the server's login, its key, setting up, and done (docs/mocks/welcome.html).</summary>
public sealed partial class ServerSetupPage : Page
{
    private static readonly string[] StepNames = ["Your server", "Confirm it is yours", "Set it up", "Ready"];

    private readonly AppServices _app = App.Services;
    private bool _keyConfirmed;
    private int? _step;
    private bool _showOwnSteps = true;
    private bool _left;
    private FirstRunSetup? _firstRun;
    private TaskCompletionSource<bool>? _trust;
    private bool _settingUp;
    private CancellationTokenSource? _setupCancel;
    private (FontIcon Icon, ProgressRing Ring)? _current;

    public ServerSetupPage()
    {
        InitializeComponent();
        // leaving while the host key is asked about is a no, and a setup still running stops: nothing waits for ever
        Unloaded += (_, _) =>
        {
            _left = true;
            _trust?.TrySetResult(false);
            _setupCancel?.Cancel();
        };
        var profile = ServerProfile.From(_app.Settings);
        HostBox.Text = profile.Host;
        UserBox.Text = profile.User;
        PortBox.Value = profile.Port;
        Show(ServerPanel, 0);
        _ = CheckSshAsync(); // the form is usable meanwhile; the bar appears if there's no client
    }

    private void Show(FrameworkElement panel, int? step = null)
    {
        foreach (var each in new FrameworkElement[] { ServerPanel, HostKeyPanel, WorkingPanel, DonePanel })
            each.Visibility = each == panel ? Visibility.Visible : Visibility.Collapsed;
        _step = step;
        ShowOwnSteps(_showOwnSteps);
        if (step is { } n)
        {
            var steps = StepNames.Select((name, i) => new SetupStep(i + 1, name,
                Done: i < n && (i != 1 || _keyConfirmed), // or it never got as far as the host key
                Current: i == n)).ToList();
            Stepper.ItemsSource = steps;
            _firstRun?.Steps(steps);
        }
        Control? main = panel == ServerPanel ? HostBox : panel == HostKeyPanel ? TrustButton : panel == DonePanel ? FinishButton : null;
        if (main is not null)
            DispatcherQueue.TryEnqueue(() => main.Focus(FocusState.Programmatic)); // once it has been laid out
    }

    /// <summary>The first run has this page instead of the plugin's page: Back and the end go to it.</summary>
    public void Attach(FirstRunSetup firstRun)
    {
        _firstRun = firstRun;
        Body.Padding = firstRun.Padding;
        Restart();
    }

    /// <summary>Back at the login, what was typed still there.</summary>
    public void Restart() => Show(ServerPanel, 0);

    /// <summary>Whether the steps show above the page; the first run has its own, beside it, when there's room.</summary>
    public void ShowOwnSteps(bool show)
    {
        _showOwnSteps = show;
        Stepper.Visibility = show && _step is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnTryAgain(object sender, RoutedEventArgs e)
    {
        Show(ServerPanel, 0);
        await CheckSshAsync();
    }

    /// <summary>Whether there is an OpenSSH client to set up with; if not, the form says why and how to fix it.</summary>
    private async Task<bool> CheckSshAsync()
    {
        var (tools, problem) = await Task.Run(() => SshTools.CheckAsync(_app.Settings.Text(ServerKeys.SshPath), CancellationToken.None));
        (SshProblem.Message, SshProblem.IsOpen) = (problem ?? "", tools is null);
        return tools is not null;
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_firstRun is { } firstRun)
            firstRun.Back();
        else
            App.Window?.ShowServerPlugin();
    }

    private void OnCancelSetup(object sender, RoutedEventArgs e)
    {
        CancelSetup.IsEnabled = false;
        _trust?.TrySetResult(false);
        _setupCancel?.Cancel();
    }

    private void OnLoginChoice(object sender, RoutedEventArgs e)
    {
        var key = LoginKey.IsChecked == true;
        PasswordBox.Visibility = key ? Visibility.Collapsed : Visibility.Visible;
        KeyPanel.Visibility = key ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnBrowseKey(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Window!.Handle);
        if (await picker.PickSingleFileAsync() is { } file)
            KeyFileBox.Text = file.Path;
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        if (_settingUp)
            return; // a second click while the key file is read, or the setup runs
        _settingUp = true;
        ServerBack.IsEnabled = false; // or the setup would carry on out of sight, asking about a host key nobody can see
        try
        {
            await ConnectAsync();
        }
        finally
        {
            _settingUp = false;
            ServerBack.IsEnabled = true;
        }
    }

    private async Task ConnectAsync()
    {
        FormError.IsOpen = false;
        if (!await CheckSshAsync())
            return; // the bar above the form says what to install; Connect checks again
        var (host, user) = (HostBox.Text.Trim(), UserBox.Text.Trim());
        if (host.Length == 0 || user.Length == 0)
        {
            Fail("Give the server and the user.");
            return;
        }
        ServerSetupRequest request;
        if (LoginKey.IsChecked == true)
        {
            var keyFile = KeyFileBox.Text.Trim();
            if (keyFile.Length == 0)
            {
                Fail("Choose the private key file.");
                return;
            }
            try
            {
                // OpenSSH reads the key itself: it is used where it is, never copied
                if (await Task.Run(() => ServerSetup.KeyNeedsPassphraseAsync(_app.Settings, keyFile, CancellationToken.None)) && PassphraseBox.Password.Length == 0)
                {
                    Fail("The key has a passphrase; type it under the file.");
                    return;
                }
            }
            catch (StageException ex)
            {
                Fail(ex.Message);
                return;
            }
            request = new ServerSetupRequest(host, (int)PortBox.Value, user, KeyFile: keyFile,
                                             Passphrase: PassphraseBox.Password.Length > 0 ? PassphraseBox.Password : null);
        }
        else
        {
            if (PasswordBox.Password.Length == 0)
            {
                Fail("Type the password (it is used once and not saved).");
                return;
            }
            request = new ServerSetupRequest(host, (int)PortBox.Value, user, Password: PasswordBox.Password);
        }
        if (!_left)
            await RunSetupAsync(request);
    }

    private void Fail(string message) => (FormError.Message, FormError.IsOpen) = (message, true);

    private async Task RunSetupAsync(ServerSetupRequest request)
    {
        Tasks.Children.Clear();
        TasksCard.Visibility = Visibility.Collapsed;
        _current = null;
        _keyConfirmed = false;
        SetupError.IsOpen = false;
        FailedButtons.Visibility = Visibility.Collapsed;
        Show(WorkingPanel, 2);
        using var cancel = new CancellationTokenSource();
        _setupCancel = cancel;
        (CancelSetup.Visibility, CancelSetup.IsEnabled) = (Visibility.Visible, true);
        var wasOnServer = _app.Settings.Mode == DownloadMode.Server;
        try
        {
            var done = await Task.Run(() => ServerSetup.RunAsync(request, _app.Store, _app.Secrets, Trust,
                step => DispatcherQueue.TryEnqueue(() => Step(step)), cancel.Token));
            MarkCurrent(failed: false);
            _app.Reload(); // mode is now server; the queue host connects to it
            if (wasOnServer)
                _ = _app.Queue.SwitchAsync(); // it was already, so nothing says to: the session open is for what was set up before
            _firstRun?.Done();
            ShowDone(done);
        }
        catch (OperationCanceledException)
        {
            MarkCurrent(failed: true);
            (SetupError.Message, SetupError.IsOpen) = ("Cancelled.", true);
            FailedButtons.Visibility = Visibility.Visible;
            Show(WorkingPanel, 2);
        }
#pragma warning disable CA1031 // whatever it was, the page says so and offers the way back, rather than spinning for ever
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (ex is not (StageException or HostKeyChangedException or AgentVersionException or SettingsException or IOException or UnauthorizedAccessException or InvalidDataException))
                CrashLog.Write("setting up the server", ex);
            MarkCurrent(failed: true);
            (SetupError.Message, SetupError.IsOpen) = (ex.Message, true);
            FailedButtons.Visibility = Visibility.Visible;
            Show(WorkingPanel, 2);
        }
        finally
        {
            _setupCancel = null;
            CancelSetup.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Asked on SSH's thread with the server's fingerprint: show it, and wait for the answer.</summary>
    private bool Trust(string fingerprint)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _trust = answer;
        DispatcherQueue.TryEnqueue(() =>
        {
            Fingerprint.Text = fingerprint;
            HostKeyLead.Text = HostKeyWords(fingerprint);
            Show(HostKeyPanel, 1);
        });
        return answer.Task.GetAwaiter().GetResult();
    }

    /// <summary>What to say about a host key: a first meeting, or a change from the key Ferry had for this very server.</summary>
    private string HostKeyWords(string fingerprint)
    {
        var (host, user) = (HostBox.Text.Trim(), UserBox.Text.Trim());
        var known = ServerProfile.From(_app.Settings);
        if (known.Host != host || known.Port != (int)PortBox.Value || known.HostKey is not { } before)
            return $"This is the first time Ferry connects to {user}@{host}. Check that its key matches before going on; if it changes later, Ferry stops and warns you.";
        return ServerProfile.SameKey(before, fingerprint)
            ? $"This is the key Ferry already has for {host}."
            : $"This is not the key Ferry has for {host}, which was {before}. Go on only if you know why it changed, such as the server being reinstalled; otherwise someone may be intercepting the connection.";
    }

    private void OnTrust(object sender, RoutedEventArgs e)
    {
        _keyConfirmed = true;
        Show(WorkingPanel, 2);
        _trust?.TrySetResult(true);
    }

    private void OnDistrust(object sender, RoutedEventArgs e)
    {
        Show(WorkingPanel, 2);
        _trust?.TrySetResult(false);
    }

    /// <summary>A new step began: the previous one is done. Each is a row of the card, with a line above all but the first.</summary>
    private void Step(string text)
    {
        MarkCurrent(failed: false);
        var content = new Grid { ColumnSpacing = 12 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var ring = new ProgressRing { Width = 16, Height = 16, IsActive = true, Foreground = ThemeBrushes.Get(ThemeBrushes.Accent, ActualTheme) };
        var icon = new FontIcon { Glyph = "\uE930", FontSize = 16, Visibility = Visibility.Collapsed };
        content.Children.Add(ring);
        content.Children.Add(icon);
        var label = new TextBlock { Text = text, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        content.Children.Add(label);
        Tasks.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SetRowStyle"],
            Padding = new Thickness(14, 8, 14, 8),
            MinHeight = 48,
            BorderThickness = new Thickness(0, Tasks.Children.Count == 0 ? 0 : 1, 0, 0),
            Child = content,
        });
        TasksCard.Visibility = Visibility.Visible;
        _current = (icon, ring);
    }

    private void MarkCurrent(bool failed)
    {
        if (_current is not { } current)
            return;
        current.Ring.IsActive = false;
        current.Ring.Visibility = Visibility.Collapsed;
        current.Icon.Visibility = Visibility.Visible;
        current.Icon.Glyph = failed ? "\uEA39" : "\uE930";
        current.Icon.Foreground = ThemeBrushes.Get(failed ? ThemeBrushes.Critical : ThemeBrushes.Success, ActualTheme);
        _current = null;
    }

    private void ShowDone(ServerSetupResult done)
    {
        DoneBar.Title = $"{done.Display} is ready";
        DoneBar.Message = done.FreeBytes is { } free ? $"The agent answered and has {Units.Size(free)} to work with." : "The agent answered.";
        Summary.Children.Clear();
        Summary.RowDefinitions.Clear();
        (string, string)[] rows =
        [
            ("Server", $"{done.Display} ({done.Rid})"),
            ("Login", done.CreatedKey ? $"Ferry's own key, readable only by you: {done.KeyFile}" : $"Your key, used where it is: {done.KeyFile}"),
            ("Host key", done.HostKey),
            ("Files on the server", _app.Settings.KeepServerFiles
                ? $"{_app.Settings.Text(ServerKeys.ServerFolder)}, kept after they're here"
                : $"{_app.Settings.Text(ServerKeys.ServerFolder)}, removed once they're here"),
            ("Files here", _app.Settings.DownloadFolder),
        ];
        for (var i = 0; i < rows.Length; i++)
        {
            Summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = rows[i].Item1, Foreground = ThemeBrushes.Get(ThemeBrushes.Secondary, ActualTheme) };
            // paths and keys are long: cut short here, whole in the tooltip
            var value = new TextBlock { Text = rows[i].Item2, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true };
            ToolTipService.SetToolTip(value, rows[i].Item2);
            Grid.SetRow(name, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            Summary.Children.Add(name);
            Summary.Children.Add(value);
        }
        Show(DonePanel, 3);
    }

    private void OnFinish(object sender, RoutedEventArgs e)
    {
        if (_firstRun is { } firstRun)
            firstRun.Finish();
        else
            App.Window?.ShowDownloads();
    }
}

/// <summary>What the first run hears from the server's setup when it has that page among its own steps.</summary>
/// <param name="Back">leaving, with nothing kept</param>
/// <param name="Done">the server is set up</param>
/// <param name="Finish">on to the app</param>
/// <param name="Steps">the steps, as they now are</param>
/// <param name="Padding">where its pages start, so nothing jumps from one step to the next</param>
/// <param name="Chosen">the settings chosen in the first run, which the server should have from the start</param>
public sealed record FirstRunSetup(Action Back, Action Done, Action Finish, Action<IReadOnlyList<SetupStep>> Steps, Thickness Padding);

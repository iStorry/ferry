using Ferry.App.Services;
using Ferry.Core;
using Ferry.Core.Settings;
using Ferry.Core.Text;
using Ferry.Plugins.Server;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Ferry.App.Pages;

/// <summary>Server Support's own page: on or off, this PC or the server, and the server's settings.</summary>
public sealed partial class ServerPluginPage : Page
{
    private const string ModeNotSaved = "The choice wasn't saved";

    private readonly AppServices _app = App.Services;
    private readonly SettingRows _rows;
    private Border? _statusCard;
    private string? _tested; // the last "Test connection" answer, shown once on the card
    private Task<(SshTools? Tools, string? Problem)>? _ssh;
    private string? _sshPath;

    public ServerPluginPage()
    {
        InitializeComponent();
        _rows = new SettingRows(_app, Header.ShowProblem, Build);
        Loaded += (_, _) =>
        {
            _app.Queue.StatusChanged += OnStatusChanged;
            Build();
        };
        Unloaded += (_, _) => _app.Queue.StatusChanged -= OnStatusChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Header.OpenedIn(this, beside: e.Parameter is true);
    }

    private FerrySettings Settings => _app.Settings;

    private void Build()
    {
        _rows.Building = true;
        Header.Toggle.IsOn = Settings.ServerSupportIsOn;
        Header.ShowTile(Header.Toggle.IsOn);
        Header.Toggle.IsEnabled = Settings[ServerKeys.Enabled].Source != SettingSource.Environment;
        var set = ServerProfile.From(Settings).IsSet;
        ShowFacts(set);
        WhereGroup.Visibility = AdvancedGroup.Visibility = set ? Visibility.Visible : Visibility.Collapsed;
        ShowMode();
        BuildServer();
        AdvancedRows.Children.Clear();
        foreach (var key in ServerKeys.Advanced)
            AdvancedRows.Children.Add(_rows.AdvancedRow(key));
        SettingRows.MarkLines(AdvancedRows);
        _rows.Building = false;
    }

    /// <summary>Under the page's name: how the plugin and the server are.</summary>
    private void ShowFacts(bool set)
    {
        Header.Facts.Children.Clear();
        Header.What = PluginCatalog.Find(PluginCatalog.ServerSupport)?.What ?? "";
        if (!Settings.ServerSupportIsOn)
            Header.Facts.Children.Add(Pill.Make("Off", ActualTheme));
        else if (!set)
            Header.Facts.Children.Add(Pill.Make("Not set up", ActualTheme));
        else if (_app.Queue.Status is ModeStatus.Connected or ModeStatus.Offline)
            Header.Facts.Children.Add(Pill.For(_app.Queue.Status, ActualTheme));
    }

    /// <summary>This PC or the server, in words, and the switch set to it.</summary>
    private void ShowMode()
    {
        var server = Settings.Mode == DownloadMode.Server;
        var locked = Settings[ServerKeys.Mode].Source == SettingSource.Environment;
        ModeChoiceHost.Child = SettingRows.ModeChoice(server, locked, OnModeChosen);
        var folder = Folders.Shorten(Settings.DownloadFolder);
        (ModeTitle.Text, ModeDetail.Text) = server
            ? ($"On {ServerProfile.From(Settings).Display}, then here",
               $"Your server downloads, even while this PC is off; finished files come to {folder} over SSH, checked with SHA-256.")
            : ("On this PC", $"Files download straight into {folder}.");
        if (locked)
            ModeDetail.Text += $" Set by {ServerKeys.Mode.Name} in the environment, so it can't be changed here.";
    }

    private void OnModeChosen(bool server)
    {
        if (_rows.Building || server == (Settings.Mode == DownloadMode.Server))
            return;
        _ = Dialogs.GuardAsync(XamlRoot, ModeNotSaved, () => ChooseModeAsync(server));
    }

    private async Task ChooseModeAsync(bool server)
    {
        if (await ServerSwitch.SetModeAsync(XamlRoot, _app, server) is { } problem)
            Header.ShowProblem(ModeNotSaved, problem);
        Build(); // the choice as it is, also when the dialog was cancelled
    }

    /// <summary>Only the status card changes, so a value being typed or a list opened elsewhere is left alone.</summary>
    private void OnStatusChanged()
    {
        var at = _statusCard is null ? -1 : ServerSection.Children.IndexOf(_statusCard);
        if (at < 0 || ServerProfile.From(Settings) is not { IsSet: true } profile)
        {
            Build();
            return;
        }
        _statusCard = StatusCard(profile);
        ServerSection.Children[at] = _statusCard;
        SettingRows.MarkLines(ServerSection);
        ShowFacts(set: true);
    }

    private void BuildServer()
    {
        ServerSection.Children.Clear();
        _statusCard = null;
        var profile = ServerProfile.From(Settings);
        if (!profile.IsSet)
        {
            var setup = new Button { Content = "Set up a server…", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            setup.Click += (_, _) => App.Window?.ShowServerSetup();
            ServerSection.Children.Add(SettingRows.Card("No server set up",
                "Needs a Linux server you can log in to with SSH. Ferry puts a small agent on it.", setup));
            SettingRows.MarkLines(ServerSection);
            return;
        }

        _statusCard = StatusCard(profile);
        ServerSection.Children.Add(_statusCard);
        ServerSection.Children.Add(SshCard());

        var keep = _rows.Control(ServerKeys.ServerKeepFiles);
        if (keep is LabeledSwitch keepSwitch)
            keepSwitch.Caption = "Keep them";
        ServerSection.Children.Add(SettingRows.Card("Files on the server", "Removed once they're here and the SHA-256 matches", keep));
        ServerSection.Children.Add(SettingRows.Card("Speed limit for bringing files here", "Such as 10M, or 0 for none. The server's own downloads follow the limit in Settings.", _rows.Control(ServerKeys.TransferLimit)));

        var forget = new Button { Content = "Forget…", Foreground = ThemeBrushes.Get(ThemeBrushes.Critical, ActualTheme) };
        forget.Click += async (_, _) =>
        {
            forget.IsEnabled = false; // removing the agent connects first: one click is enough
            try
            {
                await ForgetAsync(profile);
            }
            finally
            {
                forget.IsEnabled = true;
            }
        };
        ServerSection.Children.Add(SettingRows.Card("Forget this server",
            "Removes the login from this PC and goes back to downloading here; optionally removes the agent from the server too.", forget));
        SettingRows.MarkLines(ServerSection);
    }

    /// <summary>The server, its state, and the buttons to test it or set it up again.</summary>
    private Border StatusCard(ServerProfile profile)
    {
        var status = _app.Queue.Status;
        var title = new StackPanel { Spacing = 4 };
        title.Children.Add(new TextBlock { Text = profile.Display, FontWeight = FontWeights.SemiBold });
        var state = Pill.For(status, ActualTheme);
        state.HorizontalAlignment = HorizontalAlignment.Left;
        title.Children.Add(state);
        var tested = _tested is null ? "" : $" · Test: {_tested}";
        _tested = null;
        var detail = tested.Length > 0 && status != ModeStatus.Connected ? $"{_app.Queue.StatusDetail}{tested}" : status == ModeStatus.Connected
            ? $"{(_app.Queue.ServerFreeBytes is { } free ? $"{Units.Size(free)} free · " : "")}host key {Short(profile.HostKey)}{tested}"
            : status is ModeStatus.Offline or ModeStatus.Connecting or ModeStatus.Reconnecting or ModeStatus.Disconnecting
                ? _app.Queue.StatusDetail
                : $"Host key {Short(profile.HostKey)}";
        var test = new Button { Content = "Test connection", IsEnabled = Settings.Mode == DownloadMode.Server };
        test.Click += async (_, _) =>
        {
            test.IsEnabled = false;
            test.Content = "Testing…";
            await _app.Queue.RetryNowAsync();
            _tested = _app.Queue.Status == ModeStatus.Connected
                ? $"answered at {DateTime.Now:t}"
                : _app.Queue.Status is ModeStatus.Connecting or ModeStatus.Reconnecting ? "reconnecting" : "didn't answer";
            OnStatusChanged(); // the card again, with the answer
        };
        var again = new Button { Content = "Set up again…" };
        again.Click += (_, _) => App.Window?.ShowServerSetup();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(test);
        buttons.Children.Add(again);
        return SettingRows.Card(title, detail, buttons);
    }

    private static string Short(string? fingerprint) =>
        fingerprint is { Length: > 18 } whole ? $"{whole[..14]}…{whole[^4..]}" : fingerprint ?? "";

    /// <summary>The OpenSSH client server mode runs (or why there is none, and the fix), and the key it logs in with.</summary>
    private Border SshCard()
    {
        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = "OpenSSH client", TextWrapping = TextWrapping.Wrap });
        var state = new TextBlock
        {
            Text = "Checking…",
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = ThemeBrushes.Get(ThemeBrushes.Tertiary, ActualTheme),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        };
        title.Children.Add(state);
        var check = new Button { Content = "Check again" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(check, "Check the OpenSSH client again");
        async Task ShowAsync(bool again)
        {
            check.IsEnabled = false;
            var (tools, problem) = await SshAnswer(again);
            state.Text = tools?.Display ?? problem ?? "";
            state.Foreground = ThemeBrushes.Get(tools is null ? ThemeBrushes.Critical : ThemeBrushes.Secondary, ActualTheme);
            check.IsEnabled = true;
        }
        check.Click += async (_, _) => await ShowAsync(again: true);
        _ = ShowAsync(again: false);
        var key = Settings.Text(ServerKeys.ServerKey) is { Length: > 0 } file ? $"Logs in with {file}" : "Logs in with the key set up before";
        return SettingRows.Card(title, $"{key}. To use another client, set {ServerKeys.SshPath.Name} under Advanced.", check);
    }

    /// <summary>Whether there is a client, asked once per page: again only when asked to, or for another path. It starts ssh to find out.</summary>
    private Task<(SshTools? Tools, string? Problem)> SshAnswer(bool again)
    {
        var path = Settings.Text(ServerKeys.SshPath);
        if (again || _ssh is null || _sshPath != path)
            (_ssh, _sshPath) = (Task.Run(() => SshTools.CheckAsync(path, CancellationToken.None)), path);
        return _ssh;
    }

    private async Task ForgetAsync(ServerProfile profile)
    {
        var removeAgent = new CheckBox { Content = "Also remove the agent from the server (its files and queue stay)" };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "Ferry removes the login from this PC and downloads here again. Files still on the server stay there.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(removeAgent);
        if (!await Dialogs.ConfirmAsync(XamlRoot, $"Forget {profile.Display}?", content, "Forget", "Cancel", primaryIsDefault: false,
                                         tone: DialogTone.Danger))
            return;
        if (removeAgent.IsChecked == true)
        {
            try
            {
                await Task.Run(() => ServerSetup.RemoveAgentAsync(Settings, _app.Secrets, CancellationToken.None));
            }
            catch (Exception e) when (e is StageException or HostKeyChangedException)
            {
                await Dialogs.ShowErrorAsync(XamlRoot, "The agent was not removed", e.Message);
            }
        }
        try
        {
            ServerSetup.Forget(_app.Store, _app.Secrets);
        }
        catch (SettingsException e)
        {
            await Dialogs.ShowErrorAsync(XamlRoot, "The server wasn't forgotten", e.Message);
            return;
        }
        _app.Reload();
        Build();
    }

    private async void OnToggled(object sender, RoutedEventArgs e)
    {
        if (_rows.Building)
            return;
        var on = Header.Toggle.IsOn;
        if (await ServerSwitch.ToggleAsync(XamlRoot, _app, on) && !on && !Header.Beside)
            Header.GoBack(); // nothing here applies while it is off; the list says how to turn it on
        else
            Build(); // the switch as the plugin is, also when the dialog was cancelled
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => PageLayout.Padding = PageEdges.For(e.NewSize.Width);
}

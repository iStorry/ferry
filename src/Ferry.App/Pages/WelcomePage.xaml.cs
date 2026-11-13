using Ferry.App.Services;
using Ferry.App.ViewModels;
using Ferry.Core.Settings;
using Ferry.Plugins.Server;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Ferry.App.Pages;

/// <summary>The first run: this PC or a server, where files go, and which plugins to add (docs/mocks/welcome.html).</summary>
public sealed partial class WelcomePage : Page
{
    private static readonly string[] LocalSteps = ["Where to download", "Folder and plugins", "Ready"];
    private static readonly string[] ServerSteps = ["Where to download", "Folder and plugins", "Your server", "Confirm it is yours", "Set it up", "Ready"];

    // narrower than this, there is no room for the panel beside the page
    private const double BrandWidth = 860;

    // narrower than this, the two choices go one over the other
    private const double ChoicesWidth = 640;

    private readonly AppServices _app = App.Services;
    private ServerSetupPage? _setup;
    private bool _toServer;

    public WelcomePage()
    {
        InitializeComponent();
        Choices.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Choices.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Show(ChoosePanel);
        ActualThemeChanged += (_, _) => ShowPluginTiles();
        SizeChanged += (_, e) =>
        {
            Brand.Visibility = e.NewSize.Width < BrandWidth ? Visibility.Collapsed : Visibility.Visible;
            _setup?.ShowOwnSteps(e.NewSize.Width < BrandWidth);
            var room = e.NewSize.Width - (Brand.Visibility == Visibility.Visible ? Brand.Width : 0);
            var narrow = room < ChoicesWidth;
            Grid.SetColumn(ServerChoice, narrow ? 0 : 1);
            Grid.SetRow(ServerChoice, narrow ? 1 : 0);
            Choices.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        };
    }

    /// <summary>A step's name beside the panel: the one you are at reads stronger.</summary>
    public static Windows.UI.Text.FontWeight StepWeight(bool current) =>
        current ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    private void Show(FrameworkElement panel)
    {
        ChoosePanel.Visibility = panel == ChoosePanel ? Visibility.Visible : Visibility.Collapsed;
        PlanPanel.Visibility = panel == PlanPanel ? Visibility.Visible : Visibility.Collapsed;
        var at = panel == PlanPanel ? 1 : 0;
        BrandSteps.ItemsSource = (_toServer && at == 1 ? ServerSteps : LocalSteps)
            .Select((name, i) => new SetupStep(i + 1, name, Done: i < at, Current: i == at)).ToList();
    }

    private void OnChooseNext(object sender, RoutedEventArgs e) => ShowPlan(toServer: ServerChoice.IsChecked == true);

    /// <summary>A server is Server Support's, so that is what is asked about: the switch shows it, and the button says it is added.</summary>
    private void ShowPlan(bool toServer)
    {
        _toServer = toServer;
        ShowFolder();
        PlanTitle.Text = toServer ? "Downloads go to your server" : "Downloads go to this PC";
        PlanLead.Text = toServer
            ? "That needs the Server Support plugin. Continue to add it and set up your server."
            : "Ferry downloads links on this PC by itself. Plugins are optional.";
        PlanNext.Content = toServer ? "Add and continue" : "Start";
        ServerAdd.IsOn = toServer;
        ServerAdd.IsEnabled = !toServer;
        ServerWhat.Text = toServer
            ? "Added when your server is set up. Needed for downloading on it."
            : "Download on your own Linux server, even while this PC is off.";
        ShowPluginTiles();
        Show(PlanPanel);
    }

    private void OnPluginChosen(object sender, RoutedEventArgs e) => ShowPluginTiles();

    private void ShowPluginTiles()
    {
        PluginTile.Show(ServerTile, ServerIcon, ServerAdd.IsOn, ActualTheme);
    }

    private void OnBackToChoose(object sender, RoutedEventArgs e)
    {
        _toServer = false;
        Show(ChoosePanel);
    }

    private async void OnPlanNext(object sender, RoutedEventArgs e)
    {
        if (_toServer)
            OpenSetup();
        else if (await KeepPluginsAsync())
            App.Window?.FinishWelcome();
    }

    /// <summary>What was chosen here, kept only now: until the end of the first run, going back leaves things as they were.</summary>
    private async Task<bool> KeepPluginsAsync()
    {
        try
        {
            if (ServerAdd.IsOn && !_toServer) // the setup turns on Server Support itself, once it has worked
                _app.Change(store => store.Set(ServerKeys.Enabled.Name, "true"));
            return true;
        }
        catch (SettingsException ex)
        {
            await Dialogs.ShowErrorAsync(XamlRoot, "The plugins weren't added", ex.Message);
            return false;
        }
    }

    /// <summary>The server's setup, as the next steps of this one.</summary>
    private void OpenSetup()
    {
        if (_setup is null)
        {
            SetupFrame.Navigate(typeof(ServerSetupPage));
            _setup = (ServerSetupPage)SetupFrame.Content;
            _setup.ShowOwnSteps(ActualWidth < BrandWidth);
            _setup.Attach(new FirstRunSetup(OnSetupBack, OnSetupDone, () => App.Window?.FinishWelcome(), ShowSetupSteps, new Thickness(48, 40, 48, 32)));
        }
        else
        {
            _setup.Restart();
        }
        (ChoosePage.Visibility, SetupFrame.Visibility) = (Visibility.Collapsed, Visibility.Visible);
    }

    private void OnSetupBack()
    {
        (ChoosePage.Visibility, SetupFrame.Visibility) = (Visibility.Visible, Visibility.Collapsed);
        Show(PlanPanel);
        PlanNext.Focus(FocusState.Programmatic);
    }

    /// <summary>The server is ready, with the plugins chosen on it and saved: the first run won't be asked again.</summary>
    private void OnSetupDone()
    {
        try
        {
            _app.FinishWelcome();
        }
        catch (SettingsException e)
        {
            CrashLog.Write("saving that the welcome is done", e); // shown again next time; nothing else is lost
        }
    }

    private void ShowSetupSteps(IReadOnlyList<SetupStep> steps) =>
        BrandSteps.ItemsSource = ServerSteps.Take(2).Select((name, i) => new SetupStep(i + 1, name, Done: true, Current: false))
            .Concat(steps.Select(step => step with { Number = step.Number + 2 })).ToList();

    private async void OnChangeFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Window!.Handle);
        if (await picker.PickSingleFolderAsync() is not { } folder)
            return;
        try
        {
            _app.Change(store => store.Set(SettingKey.DownloadDir.Name, folder.Path));
            ShowFolder();
        }
        catch (SettingsException ex)
        {
            await Dialogs.ShowErrorAsync(XamlRoot, "The folder wasn't changed", ex.Message);
        }
    }

    /// <summary>A long path is cut short in its box, and is whole in the tooltip.</summary>
    private void ShowFolder()
    {
        LocalFolder.Text = _app.Settings.DownloadFolder;
        ToolTipService.SetToolTip(LocalFolder, LocalFolder.Text);
    }
}

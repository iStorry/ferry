using System.Globalization;
using Ferry.App.Services;
using Ferry.Core;
using Ferry.Core.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Ferry.App.Pages;

/// <summary>Version, updates, source, license and notices (docs/mocks/about.html). Reached from Settings; a decoy has no Settings, and this as a place of its own.</summary>
public sealed partial class AboutPage : Page
{
    // narrower than this, the update's buttons go under its words: there can be three of them
    private const double UpdateButtonsWidth = 820;

    private readonly Updater _updater = App.Services.Updater;

    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = $"Version {Product.Describe(Product.Build)} · .NET {Environment.Version.ToString(2)} · Windows App SDK";
        BackToSettings.Visibility = App.Services.IsDecoy ? Visibility.Collapsed : Visibility.Visible;
        ActualThemeChanged += (_, _) => ShowUpdate();
        Loaded += (_, _) =>
        {
            _updater.Changed += ShowUpdate;
            ShowUpdate();
        };
        Unloaded += (_, _) => _updater.Changed -= ShowUpdate;
    }

    private void OnBackToSettings(object sender, RoutedEventArgs e) => App.Window?.ShowSettings();

    private async void OnLink(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string link })
            await Launcher.LaunchUriAsync(new Uri(link));
    }

    private void ShowUpdate()
    {
        var state = _updater.State;
        UpdateRing.IsActive = state is UpdateState.Checking;
        UpdateRing.Visibility = state is UpdateState.Checking ? Visibility.Visible : Visibility.Collapsed;
        UpdateIcon.Visibility = state is UpdateState.Checking ? Visibility.Collapsed : Visibility.Visible;
        UpdateIcon.Glyph = state switch
        {
            UpdateState.Available or UpdateState.Downloading => "\uE896",
            UpdateState.Failed => "\uE7BA",
            UpdateState.UpToDate => "\uE930",
            _ => "\uE895",
        };
        // the accent only while it comes down; coral for a real problem
        var tone = state switch
        {
            UpdateState.Downloading => ThemeBrushes.Accent,
            UpdateState.UpToDate => ThemeBrushes.Success,
            UpdateState.Failed => ThemeBrushes.Critical,
            _ => null,
        };
        var brush = ThemeBrushes.Get(tone ?? ThemeBrushes.Secondary, ActualTheme);
        UpdateIcon.Foreground = brush;
        UpdateTile.Background = tone is null ? ThemeBrushes.Get(ThemeBrushes.Subtle, ActualTheme) : Pill.Wash(brush);
        UpdateTitle.FontWeight = state is UpdateState.Available or UpdateState.Downloading ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        UpdateProgress.Visibility = state == UpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.Value = _updater.Progress;
        var checkedAt = _updater.CheckedAt is { } at
            ? $"Checked {at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}"
            : "Not checked yet";
        (UpdateTitle.Text, UpdateDetail.Text) = state switch
        {
            UpdateState.Checking => ("Checking for updates…", Releases.ReleasesPage.Host + Releases.ReleasesPage.AbsolutePath),
            UpdateState.UpToDate => ("You have the latest version", checkedAt),
            UpdateState.NoRelease => ("No release is published yet", checkedAt),
            UpdateState.Available => ($"Ferry {_updater.Available?.Version.ToString(3)} is out", _updater.Message),
            UpdateState.Downloading => ($"Downloading Ferry {_updater.Available?.Version.ToString(3)}…", "Checked against SHA256SUMS.txt before it runs"),
            UpdateState.Failed => ("Couldn't check for updates", _updater.Message),
            _ => ("Updates", $"{checkedAt}. Ferry checks GitHub once a day when Settings allow."),
        };

        UpdateButtons.Children.Clear();
        if (state == UpdateState.Available)
        {
            var quiet = (Style)Application.Current.Resources["SubtleButtonStyle"];
            var skip = new Button { Content = "Skip this version", Style = quiet };
            skip.Click += (_, _) => _updater.Skip();
            var notes = new Button { Content = "What's new", Style = quiet };
            notes.Click += async (_, _) => await Launcher.LaunchUriAsync(_updater.Available!.Page);
            var install = new Button
            {
                Content = Updater.IsInstalled ? "Install and restart" : "Get it",
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            };
            install.Click += async (_, _) => await InstallAsync();
            UpdateButtons.Children.Add(skip);
            UpdateButtons.Children.Add(notes);
            UpdateButtons.Children.Add(install);
        }
        else if (state != UpdateState.Downloading)
        {
            var check = new Button { Content = "Check for updates", IsEnabled = state != UpdateState.Checking };
            check.Click += async (_, _) => await _updater.CheckAsync(manual: true);
            UpdateButtons.Children.Add(check);
        }
    }

    private async Task InstallAsync()
    {
        if (!Updater.IsInstalled)
        {
            await Launcher.LaunchUriAsync(_updater.Available?.Page ?? Releases.ReleasesPage); // a portable copy is replaced by hand
            return;
        }
        var running = App.Services.Queue.ActiveHere;
        if (running > 0 && !await Dialogs.ConfirmAsync(XamlRoot, $"{running} download{(running == 1 ? " is" : "s are")} running",
                "Ferry pauses them (what was downloaded is kept), installs the update and starts again.",
                "Pause and update", "Not now", tone: DialogTone.Caution))
            return;
        if (await _updater.DownloadAsync() is { } setup && App.Window is { } window)
            await window.InstallUpdateAsync(setup);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < PageEdges.NarrowWidth;
        PageLayout.Padding = PageEdges.For(e.NewSize.Width);

        var under = e.NewSize.Width < UpdateButtonsWidth;
        Grid.SetRow(UpdateButtons, under ? 2 : 0);
        Grid.SetColumn(UpdateButtons, under ? 1 : 2);
        Grid.SetColumnSpan(UpdateButtons, under ? 2 : 1);
        UpdateButtons.Margin = new Thickness(0, under ? 10 : 0, 0, 0);

        // the three link cards side by side, or one over the other
        LinkCards.ColumnSpacing = narrow ? 0 : 8;
        LinkCards.RowSpacing = narrow ? 8 : 0;
        for (var i = 0; i < LinkCards.Children.Count; i++)
        {
            var card = (FrameworkElement)LinkCards.Children[i];
            Grid.SetColumn(card, narrow ? 0 : i);
            Grid.SetRow(card, narrow ? i : 0);
        }
        var rest = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        (LinkCards.ColumnDefinitions[1].Width, LinkCards.ColumnDefinitions[2].Width) = (rest, rest);
    }
}

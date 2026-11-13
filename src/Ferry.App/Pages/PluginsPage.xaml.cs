using Ferry.App.Services;
using Ferry.Core.Settings;
using Ferry.Core.Sites;
using Ferry.Plugins.Server;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Ferry.App.Pages;

/// <summary>
/// What is built in, as one entry, then the plugins there is a choice about (docs/mocks/plugins.html), grouped by what each is
/// for, found by name or site, each with its switch; the one chosen shows beside the list, or in its place in a narrow window.
/// </summary>
public sealed partial class PluginsPage : Page
{
    // narrower than this, a plugin's page takes the list's place
    private const double BesideWidth = 760;

    private readonly AppServices _app = App.Services;
    private bool _building;
    private bool _narrow;
    private bool _openIfNarrow; // asked for by name, so a narrow window goes straight to it
    private string _chosen = PluginCatalog.BuiltIn;
    private PluginTab _tab = PluginTab.All;

    public PluginsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _app.SettingsChanged += Build;
            _app.Queue.StatusChanged += Build;
            _app.PluginUpdates.Changed += Build;
            _ = _app.PluginUpdates.CheckAsync();
            AddButton.Visibility = _app.IsDecoy ? Visibility.Collapsed : Visibility.Visible;
            Build();
            ShowChosen();
        };
        Unloaded += (_, _) =>
        {
            _app.SettingsChanged -= Build;
            _app.Queue.StatusChanged -= Build;
            _app.PluginUpdates.Changed -= Build;
        };
        ActualThemeChanged += (_, _) => Build();
    }

    /// <summary>Opened at a plugin, or at the first with something to set.</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string name)
        {
            // what is built in has one page between them
            var chosen = PluginCatalog.BuiltIns.Any(plugin => plugin.Name == name) ? PluginCatalog.BuiltIn : name;
            (_chosen, _openIfNarrow) = (chosen, e.NavigationMode != NavigationMode.Back); // back from it means the list
        }
    }

    private void Build()
    {
        _building = true;
        var words = FindBox.Text.Trim();
        var all = PluginCatalog.All;
        var on = all.Count(card => PluginCatalog.IsOn(card, _app.Settings));
        AllTab.Content = $"All  {all.Count}";
        OnlyOnTab.Content = $"On  {on}";
        OnlyOffTab.Content = $"Off  {all.Count - on}";
        var shown = all.Where(card => Wanted(card, words)).ToList();
        List.Items.Clear();
        var builtIn = BuiltInWanted(words);
        if (builtIn)
        {
            var row = BuiltInRow();
            List.Items.Add(row);
            if (_chosen == PluginCatalog.BuiltIn && !_narrow)
                List.SelectedItem = row;
        }
        foreach (var group in shown.GroupBy(card => card.Group))
        {
            List.Items.Add(Heading(group.Key));
            foreach (var card in group)
            {
                var row = Row(card);
                List.Items.Add(row);
                if (card.Name == _chosen && !_narrow)
                    List.SelectedItem = row;
            }
        }
        if (shown.Count == 0 && !builtIn)
            List.Items.Add(Note(words.Length > 0 ? $"No plugin reads \"{words}\". Ferry tries any link as a plain download." : "No plugins match."));
        foreach (var problem in _app.Installed.Problems)
            List.Items.Add(Note($"An installed plugin can't be used. {problem}"));
        _building = false;
    }

    private bool Wanted(PluginCard card, string words)
    {
        var on = PluginCatalog.IsOn(card, _app.Settings);
        if ((_tab == PluginTab.On && !on) || (_tab == PluginTab.Off && on))
            return false;
        return words.Length == 0 || $"{card.Name} {card.What} {string.Join(' ', card.Reads)}".Contains(words, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What is built in is on, so it isn't among those that are off; it is found by any of its sites.</summary>
    private bool BuiltInWanted(string words) =>
        _tab != PluginTab.Off && PluginCatalog.BuiltIns is { Count: > 0 } builtIns
        && (words.Length == 0 || builtIns.Any(plugin =>
            $"{PluginCatalog.BuiltIn} {plugin.Name} {plugin.Enabled.Help} {string.Join(' ', plugin.About.Reads)}".Contains(words, StringComparison.OrdinalIgnoreCase)));

    /// <summary>All that is built in, as one line with no switch: there is nothing to decide about it.</summary>
    private ListViewItem BuiltInRow()
    {
        var builtIns = PluginCatalog.BuiltIns;
        var off = builtIns.Count(plugin => !plugin.IsOn(_app.Settings));
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(8, 6, 8, 6), MinHeight = 44, Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new FontIcon { Glyph = PluginCatalog.BuiltInGlyph, FontSize = 15 };
        var tile = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(9), VerticalAlignment = VerticalAlignment.Center, Child = icon };
        PluginTile.Show(tile, icon, on: true, ActualTheme);
        grid.Children.Add(tile);

        var status = off > 0 ? $"{off} of {builtIns.Count} turned off" : $"{builtIns.Count} kinds of link · Always on";
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = PluginCatalog.BuiltIn, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis });
        words.Children.Add(new TextBlock
        {
            Text = status,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = ThemeBrushes.Get(off > 0 ? ThemeBrushes.Caution : ThemeBrushes.Secondary, ActualTheme),
        });
        Grid.SetColumn(words, 1);
        grid.Children.Add(words);
        var item = new ListViewItem { Content = grid, Tag = PluginCatalog.BuiltIn };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, $"{PluginCatalog.BuiltIn}, {status}");
        return item;
    }

    private static ListViewItem Heading(PluginGroup group) => new()
    {
        IsHitTestVisible = false,
        IsTabStop = false,
        Padding = new Thickness(8, 14, 8, 6),
        Content = new TextBlock
        {
            Text = group switch
            {
                PluginGroup.Links => "Links it can read",
                PluginGroup.Around => "Around your downloads",
                _ => "Where downloads run",
            },
            Style = (Style)Application.Current.Resources["CaptionFaintStyle"],
        },
    };

    /// <summary>A line in the list that is no plugin.</summary>
    private static ListViewItem Note(string text) => new()
    {
        IsHitTestVisible = false,
        IsTabStop = false,
        Padding = new Thickness(16, 32, 16, 32),
        Content = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Style = (Style)Application.Current.Resources["CaptionMutedStyle"],
        },
    };

    /// <summary>A plugin in the list: its tile, how it stands, and its switch, which works without opening anything.</summary>
    private ListViewItem Row(PluginCard card)
    {
        var on = PluginCatalog.IsOn(card, _app.Settings);
        var locked = _app.Settings[EnabledKey(card)].Source == SettingSource.Environment;
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(8, 6, 8, 6), MinHeight = 44 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon { Glyph = card.Glyph, FontSize = 15 };
        var tile = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(9), VerticalAlignment = VerticalAlignment.Center, Child = icon };
        PluginTile.Show(tile, icon, on, ActualTheme);
        grid.Children.Add(tile);

        var (status, tone) = Status(card, on, locked);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = card.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis });
        words.Children.Add(new TextBlock
        {
            Text = status,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = ThemeBrushes.Get(tone, ActualTheme),
        });
        Grid.SetColumn(words, 1);
        grid.Children.Add(words);

        var toggle = new ToggleSwitch
        {
            IsOn = on,
            IsEnabled = !locked,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
            Margin = new Thickness(0, 0, -12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, card.Name);
        toggle.Toggled += async (_, _) =>
        {
            if (_building)
                return;
            await ToggleAsync(card, toggle.IsOn);
            Build(); // the switches as the plugins are, also when the dialog was cancelled
            ShowChosen();
        };
        Grid.SetColumn(toggle, 2);
        grid.Children.Add(toggle);
        var item = new ListViewItem { Content = grid, Tag = card.Name };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, $"{card.Name}, {status}");
        return item;
    }

    private Task ToggleAsync(PluginCard card, bool on) => card.Name switch
    {
        PluginCatalog.ServerSupport => ToggleServerAsync(on),
        _ => PluginSwitch.ToggleAsync(XamlRoot, _app, card.Plugin!, card.What, on),
    };

    private async Task ToggleServerAsync(bool on)
    {
        if (await ServerSwitch.ToggleAsync(XamlRoot, _app, on) && on)
            _chosen = PluginCatalog.ServerSupport; // what it needs next, a server, is there
    }

    private static SettingKey EnabledKey(PluginCard card) => card.Name switch
    {
        PluginCatalog.ServerSupport => ServerKeys.Enabled,
        _ => card.Plugin!.Enabled,
    };

    private (string Words, string Tone) Status(PluginCard card, bool on, bool locked)
    {
        if (locked)
            return ($"{(on ? "On" : "Off")} · Set by {EnabledKey(card).Name}", ThemeBrushes.Secondary);
        if (!on)
            return ("Off", ThemeBrushes.Secondary);
        if (card.Plugin is { } installed && _app.Installed.Find(installed) is { } package)
            return OnServer(package);
        if (card.Name == PluginCatalog.ServerSupport)
            return ServerProfile.From(_app.Settings) is { IsSet: true } profile ? ($"On · {profile.Display}", ThemeBrushes.Secondary) : ("Needs a server", ThemeBrushes.Caution);
        return card.Plugin is { Secrets.Count: > 0 } plugin && !plugin.Secrets.Any(secret => _app.Secrets.Has(secret.Name))
            ? ($"Needs its {plugin.Secrets[0].Title}", ThemeBrushes.Caution)
            : ("On", ThemeBrushes.Secondary);
    }

    /// <summary>How an installed plugin that is on stands: it runs on the server, so that is where to look.</summary>
    private (string Words, string Tone) OnServer(InstalledPlugin package)
    {
        if (_app.PluginUpdates.For(package) is { } update)
            return ($"Update available · {update.Version}", ThemeBrushes.Accent);
        if (_app.Settings.Mode != DownloadMode.Server)
            return ("Needs your server", ThemeBrushes.Caution);
        var id = package.Stored.Manifest.Id;
        return _app.Queue.ServerPlugins.FirstOrDefault(plugin => plugin.Id == id) switch
        {
            null => ("On · Goes to your server when it connects", ThemeBrushes.Secondary),
            { Problem: not null } => ("Not running on your server", ThemeBrushes.Caution),
            _ => ("On · On your server", ThemeBrushes.Secondary),
        };
    }

    private async void OnAddFromFile(object sender, RoutedEventArgs e) => Added(await PluginInstall.FromFileAsync(XamlRoot, _app));

    private async void OnAddFromLink(object sender, RoutedEventArgs e) => Added(await PluginInstall.FromLinkAsync(XamlRoot, _app));

    /// <summary>A plugin just installed is the one chosen, so what it needs next is in view.</summary>
    private void Added(string? name)
    {
        if (name is null)
            return;
        _chosen = name;
        Build();
        ShowChosen();
    }

    private void OnChosen(object sender, SelectionChangedEventArgs e)
    {
        if (_building || List.SelectedItem is not ListViewItem { Tag: string name })
            return;
        _chosen = name;
        if (_narrow)
            OpenInstead();
        else
            ShowChosen();
    }

    /// <summary>The chosen plugin's page, beside the list.</summary>
    private void ShowChosen()
    {
        if (_narrow || PageOf(_chosen, beside: true) is not (var page, var parameter))
            return;
        Detail.Navigate(page, parameter, new SuppressNavigationTransitionInfo());
        Detail.BackStack.Clear(); // one plugin replaces the other: there is no going back through them
    }

    /// <summary>The chosen plugin's page, in the list's place: a narrow window has no room beside it.</summary>
    private void OpenInstead()
    {
        if (PageOf(_chosen, beside: false) is not (var page, var parameter))
            return;
        Frame.Navigate(page, parameter, new DrillInNavigationTransitionInfo());
    }

    /// <summary>The page of what is chosen; null once it is gone, as a plugin that was removed is.</summary>
    private static (Type Page, object Parameter)? PageOf(string chosen, bool beside) => chosen switch
    {
        PluginCatalog.BuiltIn => (typeof(BuiltInPage), beside),
        PluginCatalog.ServerSupport => (typeof(ServerPluginPage), beside),
        _ when PluginCatalog.Find(chosen) is { Plugin: { } plugin } => (typeof(PluginOptionsPage), new PluginPageArgs(plugin, beside)),
        _ => null,
    };

    private void OnTab(object sender, RoutedEventArgs e)
    {
        _tab = ReferenceEquals(sender, OnlyOnTab) ? PluginTab.On : ReferenceEquals(sender, OnlyOffTab) ? PluginTab.Off : PluginTab.All;
        Build();
    }

    private void OnFindChanged(object sender, TextChangedEventArgs e) => Build();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < BesideWidth;
        var changed = narrow != _narrow;
        _narrow = narrow;
        ListColumn.Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(348);
        DetailColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Detail.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        if (narrow && _openIfNarrow)
        {
            _openIfNarrow = false;
            OpenInstead();
        }
        else if (changed && !narrow && IsLoaded)
        {
            Build();
            ShowChosen();
        }
    }

    private enum PluginTab
    {
        All,
        On,
        Off,
    }
}

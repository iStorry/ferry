using Ferry.App.Services;
using Ferry.Core.Settings;
using Ferry.Core.Sites;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Navigation;

namespace Ferry.App.Pages;

/// <summary>
/// What is part of Ferry (<see cref="SitePlugin.BuiltIn"/>), on one page in place of a page and a switch each: what each
/// reads, the options between them, and what to expect. One that the settings file or the environment has turned off
/// says so here, with the way to turn it back on.
/// </summary>
public sealed partial class BuiltInPage : Page
{
    private readonly AppServices _app = App.Services;
    private readonly SettingRows _rows;

    public BuiltInPage()
    {
        InitializeComponent();
        _rows = new SettingRows(_app, Header.ShowProblem, Build);
        Header.Toggle.Visibility = Visibility.Collapsed; // there is nothing to decide
        ActualThemeChanged += (_, _) => Build();
    }

    /// <summary>Opened beside the list on the Plugins page, or in its place.</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Header.OpenedIn(this, e.Parameter is true);
        Build();
    }

    private void Build()
    {
        _rows.Building = true;
        var builtIns = PluginCatalog.BuiltIns;
        var off = builtIns.Count(plugin => !plugin.IsOn(_app.Settings));
        Header.Title = PluginCatalog.BuiltIn;
        Header.What = "Links Ferry reads by itself. There is nothing to turn on or set up.";
        Header.Glyph = PluginCatalog.BuiltInGlyph;
        Header.ShowTile(on: true);
        Header.Facts.Children.Clear();
        Header.Facts.Children.Add(off > 0
            ? Pill.Make($"{off} turned off", ActualTheme, ThemeBrushes.Caution, "")
            : Pill.Make("Always on", ActualTheme, ThemeBrushes.Accent));

        Readers.Children.Clear();
        foreach (var plugin in builtIns)
            Readers.Children.Add(ReaderRow(plugin));
        SettingRows.MarkLines(Readers);

        Rows.Children.Clear();
        foreach (var key in builtIns.SelectMany(plugin => plugin.Options))
            Rows.Children.Add(_rows.AdvancedRow(key));
        SettingRows.MarkLines(Rows);
        OptionsGroup.Visibility = Rows.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        Limits.Children.Clear();
        foreach (var plugin in builtIns.Where(plugin => plugin.About.Limits.Count > 0))
            Limits.Children.Add(LimitLine(plugin));
        LimitsGroup.Visibility = Limits.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _rows.Building = false;
    }

    /// <summary>One of them: its icon and name, what it reads, and the way back on if it was turned off.</summary>
    private Border ReaderRow(SitePlugin plugin)
    {
        var on = plugin.IsOn(_app.Settings);
        var locked = _app.Settings[plugin.Enabled].Source == SettingSource.Environment;
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new FontIcon
        {
            Glyph = plugin.About.Glyph,
            FontSize = 14,
            Foreground = ThemeBrushes.Get(on ? ThemeBrushes.Accent : ThemeBrushes.Secondary, ActualTheme),
        });
        title.Children.Add(new TextBlock { Text = plugin.Name });
        if (on)
            return SettingRows.Card(title, plugin.Enabled.Help);
        if (locked)
            return SettingRows.Card(title, $"{plugin.Enabled.Help} Turned off by {plugin.Enabled.Name} in the environment, so it can't be changed here.", Pill.Make("Off", ActualTheme));
        var turnOn = new Button { Content = "Turn on" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(turnOn, $"Turn on {plugin.Name}");
        turnOn.Click += async (_, _) =>
        {
            await PluginSwitch.SaveAsync(XamlRoot, _app, plugin.Enabled, plugin.Name, on: true);
            Build();
        };
        return SettingRows.Card(title, $"{plugin.Enabled.Help} Turned off in your settings.", turnOn);
    }

    /// <summary>What to expect of one of them, after its name.</summary>
    private TextBlock LimitLine(SitePlugin plugin)
    {
        var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrushes.Get(ThemeBrushes.Secondary, ActualTheme) };
        line.Inlines.Add(new Run { Text = $"{plugin.Name}  ", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        line.Inlines.Add(new Run { Text = string.Join(' ', plugin.About.Limits) });
        return line;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => PageLayout.Padding = PageEdges.For(e.NewSize.Width);
}

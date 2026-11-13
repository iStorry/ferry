using System.Text;
using Ferry.App.Services;
using Ferry.Core.Settings;
using Ferry.Core.Sites;
using Ferry.Plugins.BrowserLink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Ferry.App.Pages;

/// <summary>What opens a plugin's page: which, and whether it sits beside the list on the Plugins page rather than instead of it.</summary>
public sealed record PluginPageArgs(SitePlugin Plugin, bool Beside);

/// <summary>
/// A plugin's page (docs/mocks/plugins.html): its switch, what it reads, its secrets, which are only ever written or removed here,
/// its settings from its part of the table, and what it can't do yet.
/// </summary>
public sealed partial class PluginOptionsPage : Page
{
    private readonly AppServices _app = App.Services;
    private readonly SettingRows _rows;
    private SitePlugin? _plugin;

    public PluginOptionsPage()
    {
        InitializeComponent();
        _rows = new SettingRows(_app, Header.ShowProblem, Build);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is PluginPageArgs args)
        {
            _plugin = args.Plugin;
            Header.OpenedIn(this, args.Beside);
        }
        Build();
    }

    private void Build()
    {
        if (_plugin is not { } plugin || PluginCatalog.Find(plugin.Name) is not { } card)
            return;
        _rows.Building = true;
        var on = plugin.IsOn(_app.Settings);
        var locked = _app.Settings[plugin.Enabled].Source == SettingSource.Environment;
        Header.Title = plugin.Name;
        Header.What = plugin.Enabled.Help;
        Header.Glyph = card.Glyph;
        Header.ShowTile(on);
        Header.Toggle.IsOn = on;
        Header.Toggle.IsEnabled = !locked;

        Header.Facts.Children.Clear();
        var needs = on && plugin.Secrets.Count > 0 && !plugin.Secrets.Any(secret => _app.Secrets.Has(secret.Name));
        Header.Facts.Children.Add(!on ? Pill.Make("Off", ActualTheme)
            : needs ? Pill.Make($"Needs its {plugin.Secrets[0].Title}", ActualTheme, ThemeBrushes.Caution, "\uE8D7")
            : Pill.Make("On", ActualTheme, ThemeBrushes.Accent));
        if (locked)
            Header.Facts.Children.Add(Pill.Make($"Set by {plugin.Enabled.Name}", ActualTheme));

        Reads.Children.Clear();
        foreach (var read in card.Reads)
            Reads.Children.Add(Pill.Make(read, ActualTheme));
        ReadsGroup.Visibility = Shown(card.Reads.Count > 0);

        KeyRows.Children.Clear();
        if (plugin == BrowserLinkPlugin.Plugin)
            KeyRows.Children.Add(ExtensionCard());
        foreach (var secret in plugin.Secrets)
            KeyRows.Children.Add(SecretCard(secret));
        SettingRows.MarkLines(KeyRows);
        KeysGroup.Visibility = Shown(KeyRows.Children.Count > 0);

        Rows.Children.Clear();
        foreach (var key in plugin.Options)
            Rows.Children.Add(_rows.AdvancedRow(key));
        SettingRows.MarkLines(Rows);
        OptionsGroup.Visibility = Shown(plugin.Options.Count > 0);

        Limits.Children.Clear();
        foreach (var limit in card.Limits)
            Limits.Children.Add(new TextBlock { Text = limit, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrushes.Get(ThemeBrushes.Secondary, ActualTheme) });
        LimitsGroup.Visibility = Shown(card.Limits.Count > 0);

        InstalledRows.Children.Clear();
        if (_app.Installed.Find(plugin) is { } installed)
            InstalledRows.Children.Add(InstalledCard(installed));
        InstalledGroup.Visibility = Shown(InstalledRows.Children.Count > 0);
        _rows.Building = false;
    }

    private static Visibility Shown(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    private async void OnToggled(object sender, RoutedEventArgs e)
    {
        if (_rows.Building || _plugin is not { } plugin)
            return;
        await PluginSwitch.ToggleAsync(XamlRoot, _app, plugin, plugin.Enabled.Help, Header.Toggle.IsOn);
        Build(); // the switch as the plugin is, also when the dialog was cancelled
    }

    private Border SecretCard(PluginSecret secret)
    {
        var saved = _app.Secrets.Has(secret.Name);
        var box = new PasswordBox { Width = 200, PlaceholderText = saved ? "Saved. Type to replace it" : "Not set" };
        var save = new Button { Content = "Save" };
        save.Click += (_, _) =>
        {
            if (box.Password.Trim() is { Length: > 0 } value)
                ChangeSecret(() => _app.Secrets.Save(secret.Name, Encoding.UTF8.GetBytes(value)));
        };
        var remove = new Button { Content = "Remove", IsEnabled = saved, Style = (Style)Application.Current.Resources["SubtleButtonStyle"] };
        remove.Click += (_, _) => ChangeSecret(() => _app.Secrets.Delete(secret.Name));
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        controls.Children.Add(box);
        controls.Children.Add(save);
        controls.Children.Add(remove);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, secret.Title);
        return SettingRows.Card(secret.Title, secret.Help, controls);
    }

    /// <summary>Whose an installed plugin is, how it does on the server, and the way to take it off again.</summary>
    private Border InstalledCard(InstalledPlugin installed)
    {
        var (manifest, package) = (installed.Stored.Manifest, installed.Stored.Package);
        var signed = package.SignedBy is { } signer ? $"signed by key {signer[..16]}" : "not signed";
        var onServer = _app.Queue.ServerPlugins.FirstOrDefault(plugin => plugin.Id == manifest.Id)?.Problem is { } problem
            ? $" Your server couldn't run it: {problem}"
            : "";
        var remove = new Button { Content = "Remove" };
        remove.Click += async (_, _) =>
        {
            if (await PluginInstall.RemoveAsync(XamlRoot, _app, installed))
                App.Window?.ShowPlugins();
        };
        return SettingRows.Card($"Version {manifest.Version}, by {manifest.Publisher}", $"It runs on your server, never on this PC. It is {signed}.{onServer}", remove);
    }

    /// <summary>What uses a secret reads it when the settings are read, so they are read again.</summary>
    private void ChangeSecret(Action change)
    {
        try
        {
            change();
            _app.Reload();
            Build();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Header.ShowProblem("That wasn't saved", e.Message);
        }
    }

    /// <summary>Browsers only take an extension from the user, so this says how, and shows where it is.</summary>
    private static Border ExtensionCard()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "extension");
        var open = new Button { Content = "Show folder" };
        open.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        return SettingRows.Card("The extension",
            "In Chrome or Edge: open the extensions page, turn on developer mode, choose Load unpacked, and pick this folder. Then right-click a link.", open);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => PageLayout.Padding = PageEdges.For(e.NewSize.Width);
}

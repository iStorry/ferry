using Ferry.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace Ferry.App.Pages;

/// <summary>The top of a plugin's page. The page says what it is, what is on its switch and in its facts; the way back and the tile are kept here.</summary>
public sealed partial class PluginHeader : UserControl
{
    private Page? _page;

    public PluginHeader()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (!Beside)
                BackButton.Focus(FocusState.Programmatic); // or it goes to the first thing that can take it, the box at the top
        };
    }

    /// <summary>The plugin's name, which is also what a screen reader calls its switch.</summary>
    public string Title
    {
        get => Heading.Text;
        set
        {
            Heading.Text = value;
            Toggle.AccessibleName = value;
        }
    }

    public string Glyph
    {
        get => TileIcon.Glyph;
        set => TileIcon.Glyph = value;
    }

    /// <summary>What the plugin does, in a line under its name.</summary>
    public string What
    {
        get => Summary.Text;
        set => Summary.Text = value;
    }

    /// <summary>The page sits beside the list on the Plugins page rather than instead of it, so there is no going back to the list.</summary>
    public bool Beside { get; private set; }

    public event RoutedEventHandler? Toggled
    {
        add => Toggle.Toggled += value;
        remove => Toggle.Toggled -= value;
    }

    /// <param name="page">the page it heads, whose frame it goes back in</param>
    /// <param name="beside">see <see cref="Beside"/></param>
    public void OpenedIn(Page page, bool beside)
    {
        (_page, Beside) = (page, beside);
        BackButton.Visibility = beside ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ShowTile(bool on) => PluginTile.Show(Tile, TileIcon, on, ActualTheme);

    public void ShowProblem(string title, string message) => (Problem.Title, Problem.Message, Problem.IsOpen) = (title, message, true);

    public void GoBack() => _page?.Frame.GoBack(new DrillInNavigationTransitionInfo());

    private void OnBack(object sender, RoutedEventArgs e) => GoBack();
}

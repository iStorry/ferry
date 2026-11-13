using Ferry.App.Services;
using Ferry.App.ViewModels;
using Ferry.Core.Queue;
using Ferry.Plugins.Server;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Ferry.App.Pages;

/// <summary>
/// The main page (docs/mocks/downloads.html): how it is going, then the queue in sections (Needs you, Now,
/// Up next, Paused, Done) with a tab for each, and a panel for a file up close. Links come from the window's command box, a
/// paste or a drop. The page is kept between visits. This part is its life; the others are how links are added
/// (Adding), the rows (List), the top of the page (Hero), what is selected (Panel) and what can be done with a file (Actions).
/// </summary>
public sealed partial class DownloadsPage : Page
{
    private readonly AppServices _app = App.Services;
    private bool _onScreen; // loaded: it hears from the queue, and its list can be scrolled
    private Guid? _reveal; // to bring into view once the list has it

    public DownloadsPage()
    {
        InitializeComponent();
        _context = CurrentContext();
        Sections.Source = _shown;
        List.ItemsSource = Sections.View;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => UpdateStatus();
        // the ListView takes Space for selection and marks it handled: listen to handled keys too
        List.AddHandler(KeyDownEvent, new KeyEventHandler(OnListKeyDown), handledEventsToo: true);
        // a row's container is what a screen reader names: the file, its state and its detail
        List.ContainerContentChanging += (_, args) =>
        {
            if (args.InRecycleQueue)
            {
                if (args.Item is ItemViewModel row)
                    row.Hovered = false; // scrolled or moved away under the pointer: no leaving it to say so
                return;
            }
            if (args.Item?.ToString() is { } words)
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer, words);
        };
        ThemeBrushes.ContrastChanged += OnContrastChanged;
        BuildTabs();
        _newsTimer = DispatcherQueue.CreateTimer();
        _newsTimer.Interval = Throttle.Every;
        _newsTimer.IsRepeating = false;
        _newsTimer.Tick += (_, _) => ShowNews();
    }

    private IDownloadQueue? Queue => _app.Queue.Queue;

    private bool ServerMode => _app.Settings.Mode == DownloadMode.Server;

    /// <summary>Where downloads run is the server, connected or not. A remote queue isn't: its files stay on its machine.</summary>
    private bool OnServer => _app.Queue.Where == DownloadPlace.Server;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_onScreen)
            return; // navigating to the page it is on can load it again before it was unloaded
        _onScreen = true;
        _app.Queue.Changed += OnQueueChanged;
        _app.Queue.QueueReplaced += Rebuild;
        _app.Queue.StatusChanged += UpdateStatus;
        _app.SettingsChanged += UpdateStatus;
        _app.Speed.Sampled += OnSpeedSampled;
        _app.Queue.WhenDoneChanged += ShowWhenDone;
        ShowWhenDone();
        SayRowsAgain();
        // another queue took over meanwhile: its revisions start again, so rows of the last one would ignore its news
        if (ReferenceEquals(_rowsOf, Queue))
            CatchUp();
        else
            Rebuild();
        UpdateStatus();
        OnSpeedSampled();
        _ = AddHeldLinksAsync();
        if (App.Window?.TakePendingOffer() is { Count: > 0 } offer)
            Offer(offer);
        if (_reveal is { } id)
            Reveal(id);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            return; // the last of an old visit, heard after the new one's load
        _onScreen = false;
        _app.Queue.Changed -= OnQueueChanged;
        _app.Queue.QueueReplaced -= Rebuild;
        _app.Queue.StatusChanged -= UpdateStatus;
        _app.SettingsChanged -= UpdateStatus;
        _app.Speed.Sampled -= OnSpeedSampled;
        _app.Queue.WhenDoneChanged -= ShowWhenDone;
        _newsTimer.Stop();
        _news.Clear(); // caught up with on coming back
        foreach (var row in _byId.Values)
            row.Hovered = false; // the pointer's leaving won't be heard
    }

    /// <summary>This file, selected and in view.</summary>
    public void Reveal(Guid id)
    {
        _reveal = null;
        ShowNews();
        if (!_onScreen || !_byId.TryGetValue(id, out var row))
        {
            _reveal = id; // the list comes when the page has loaded
            return;
        }
        // the box's own change comes later, and the row must be in the list by now
        FilterBox.Text = "";
        foreach (var group in _groups)
            group.Filter = "";
        ChooseTab(null);
        var section = GroupOf(row);
        if (section.Strip is { ShowingFiles: false })
            section.ToggleFiles();
        if (!_shown.Contains(section) || !section.Contains(row))
            return; // what needs you doesn't show while the server can't be reached
        List.SelectedItems.Clear();
        List.SelectedItems.Add(row);
        List.ScrollIntoView(row);
    }

    private void Show(InfoBarSeverity severity, string title, string message)
    {
        (AddResult.Severity, AddResult.Title, AddResult.Message, AddResult.IsOpen) = (severity, title, message, true);
    }
}

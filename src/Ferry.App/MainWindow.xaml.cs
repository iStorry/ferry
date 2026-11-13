using Ferry.App.Pages;
using Ferry.App.Services;
using Ferry.Core.AppLocking;
using Ferry.Core.Queue;
using Ferry.Core.Settings;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace Ferry.App;

/// <summary>
/// The one window: the title bar with the command box, the sidebar with where downloads happen in its foot, the first-run
/// Welcome page, and the PIN asked for before any of it when the app lock is on.
/// </summary>
public sealed partial class MainWindow : Window
{
    // the real session's until the PIN is in; then the session that opened, which is a decoy's after too many wrong PINs
    private AppServices _app = App.Services;
    private PluginHost? _plugins;
    private bool _sessionStarted;
    private bool _queueRead; // the Downloads page has something true to show
    private bool _goingToPage; // a place is being marked for a page that is navigated to with what it needs
    private readonly Throttle _progress;

    public MainWindow()
    {
        InitializeComponent();
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Taskbar = new Taskbar(Handle);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        WindowPlacement.Restore(AppWindow, Handle, _app);

        ApplyTheme();
        // the taskbar follows at most four times a second, not once per progress event of every file
        _progress = new Throttle(DispatcherQueue, UpdateProgress);
        Root.ActualThemeChanged += (_, _) => Repaint();
        ThemeBrushes.FollowHighContrast(AppWindow.Id);
        ThemeBrushes.ContrastChanged += Repaint;
        var locked = _app.Lock.Status != LockStatus.Off;
        // the caption buttons' width and the clickable parts of the title bar follow the window's scale
        Root.Loaded += (_, _) =>
        {
            Root.XamlRoot.Changed += (_, _) => UpdateTitleBar();
            UpdateTitleBar();
            if (!locked)
                _ = OpenSessionAsync();
        };
        AppWindow.Closing += OnClosing;
        // the box's own text field marks the press as handled
        CommandBox.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnCommandPressed), handledEventsToo: true);

        if (locked)
            ShowLock();
    }

    public nint Handle { get; }

    public Taskbar Taskbar { get; }

    /// <summary>The PIN was right, or too many were wrong (<paramref name="decoy"/>): open the session it calls for.</summary>
    /// <exception cref="IOException">the decoy's folder can't be made; the window stays locked</exception>
    /// <exception cref="SettingsException">the decoy's settings can't be written; the window stays locked</exception>
    public void Unlock(bool decoy)
    {
        if (decoy)
            App.UseDecoy();
        LockFrame.Visibility = Visibility.Collapsed;
        LockFrame.Content = null;
        _ = OpenSessionAsync();
    }

    /// <summary>
    /// The session, once what is on screen has been drawn, so the window is there at once. If it can't open, that is said
    /// and the app ends: this process would otherwise keep the single-instance key, and every later launch would go to it.
    /// </summary>
    private async Task OpenSessionAsync()
    {
        try
        {
            await IdleAsync();
            if (!_closeConfirmed)
                await StartSessionAsync();
        }
#pragma warning disable CA1031 // whatever stopped it, the window has nothing to show: it can only say why and close
        catch (Exception e)
#pragma warning restore CA1031
        {
            CrashLog.Write("starting", e);
            await Dialogs.ShowErrorAsync(Root.XamlRoot, "Ferry couldn't start", e.Message);
            _closeConfirmed = true;
            Close();
        }
    }

    /// <summary>
    /// Load the queue and show the window's own parts. Not before the lock is opened: until then nothing
    /// downloads and nothing of the user's is read but the settings. A decoy has only Downloads and About.
    /// The sidebar comes first, as it says where downloads happen before the queue has been read; the Downloads page after
    /// that, as an empty list would say there is nothing in it.
    /// </summary>
    private async Task StartSessionAsync()
    {
        _app = App.Services;
        _sessionStarted = true;
        ApplyTheme();
        _app.SettingsChanged += ApplyTheme;
        _app.SettingsChanged += UpdatePlugins;
        _app.Queue.StatusChanged += UpdateModeLine;
        _app.Queue.StatusChanged += OnStatusChanged;
        _app.Queue.Changed += OnQueueChanged;
        _app.Queue.QueueReplaced += OnQueueReplaced;
        _room.Changed += () => ShowRoom(_app.Queue);

        var queue = _app.Queue.StartAsync();
        _app.Speed.Start();
        UpdateModeLine();
        UpdatePlugins();
        var place = _app.IsDecoy ? Visibility.Collapsed : Visibility.Visible;
        (PluginsItem.Visibility, SettingsItem.Visibility) = (place, place);
        AboutItem.Visibility = _app.IsDecoy ? Visibility.Visible : Visibility.Collapsed;
        if (_app.ShowWelcome)
            ShowWelcome();
        else
            ShowShell(true);
        await Task.WhenAll(queue, IdleAsync());
        if (_closeConfirmed)
            return;

        _queueRead = true;
        _app.Browse.Changed += UpdatePlugins;
        _app.StartBrowsing();
        _app.Updater.Changed += UpdateUpdateLine;
        _app.Updater.CheckDailyInBackground();
        new ClipboardWatcher(Handle).Changed += OnClipboardChanged;
        StartTray();
        if (!_app.IsDecoy)
        {
            var plugins = new PluginHost(_app, (title, message) => DispatcherQueue.TryEnqueue(() => _tray?.Notify(title, message)));
            plugins.Start();
            Closed += (_, _) => plugins.Dispose();
            _plugins = plugins;
        }
        if (WelcomeFrame.Visibility == Visibility.Collapsed && ContentFrame.Content is null)
            ShowDownloads();
        else
            TakeHeldLinksToDownloads(); // a place was chosen meanwhile, and links came
    }

    /// <summary>Done once the window has drawn what is waiting to be drawn.</summary>
    private Task IdleAsync()
    {
        var idle = new TaskCompletionSource();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, idle.SetResult);
        return idle.Task;
    }

    private void OnQueueChanged(QueueEvent e)
    {
        if (e is QueueItemRemoved removed)
            _offered.Remove(removed.Item.Link.Key);
        var finished = _batch.Note(e);
        _progress.Ask();
        if (finished is not null)
            OnBatchFinished(finished);
    }

    /// <summary>Another queue took over: what the last one was doing isn't a batch that finished.</summary>
    private void OnQueueReplaced()
    {
        _batch.Reset(_app.Queue.Queue?.Items ?? []);
        _offered.Clear();
        UpdateProgress();
    }

    private void ShowLock()
    {
        ShowShell(false);
        LockFrame.Visibility = Visibility.Visible;
        LockFrame.Navigate(typeof(LockPage));
    }

    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        AppWindow.Show();
        Activate();
    }

    public void ShowDownloads() => Show(DownloadsItem, new SuppressNavigationTransitionInfo());

    /// <summary>Settings, also when Settings is already the place selected (back from the server setup).</summary>
    public void ShowSettings() => Show(SettingsItem, new DrillInNavigationTransitionInfo());

    /// <summary>Settings, at a section of it.</summary>
    public void ShowSettings(string section) => GoTo(SettingsItem, typeof(SettingsPage), section);

    /// <summary>About, reached from Settings, or the place of its own a decoy has.</summary>
    public void ShowAbout() => GoTo(_app.IsDecoy ? AboutItem : SettingsItem, typeof(AboutPage), null);

    public void ShowPlugins() => Show(PluginsItem, new DrillInNavigationTransitionInfo());

    /// <summary>Plugins, with that plugin chosen.</summary>
    public void ShowPlugin(string name) => GoTo(PluginsItem, typeof(PluginsPage), name);

    public void ShowBrowser() => Show(BrowserItem, new DrillInNavigationTransitionInfo());

    public void ShowLibrary() => Show(LibraryItem, new DrillInNavigationTransitionInfo());

    /// <summary>The server setup, inside the window (from the plugin's page, or a bar saying there is no server).</summary>
    public void ShowServerSetup() => GoTo(PluginsItem, typeof(ServerSetupPage), null);

    /// <summary>Server Support's own page, in Plugins.</summary>
    public void ShowServerPlugin() => ShowPlugin(PluginCatalog.ServerSupport);

    /// <summary>A page that isn't a place's own, or a place's with something to show: the place marked, and the page opened once.</summary>
    private void GoTo(RadioButton item, Type page, object? parameter)
    {
        _goingToPage = true;
        item.IsChecked = true;
        _goingToPage = false;
        Open(page, parameter, new DrillInNavigationTransitionInfo());
    }

    /// <summary>Go to a place in the sidebar; already there, show its page again. A place it hides can't be gone to.</summary>
    private void Show(RadioButton item, NavigationTransitionInfo transition)
    {
        if (item.Visibility != Visibility.Visible)
            return;
        if (item.IsChecked == true)
            Open(PageFor(item), null, transition);
        else
            item.IsChecked = true;
    }

    /// <summary>A page reached from outside the page before it, which it has no way back to: the frame keeps no list of them.</summary>
    private void Open(Type page, object? parameter, NavigationTransitionInfo transition)
    {
        ContentFrame.Navigate(page, parameter, transition);
        ContentFrame.BackStack.Clear();
    }

    private void OnPlaceChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton item && !_goingToPage)
            Open(PageFor(item), null, new EntranceNavigationTransitionInfo());
    }

    /// <summary>A place already marked, from a page further in (About, in Settings): back to the place's own page.</summary>
    private void OnPlaceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton item && ContentFrame.Content?.GetType() != PageFor(item))
            Open(PageFor(item), null, new EntranceNavigationTransitionInfo());
    }

    private static Type PageFor(RadioButton item) => item.Tag switch
    {
        "About" => typeof(AboutPage),
        "Browser" => typeof(BrowserPage),
        "Library" => typeof(LibraryPage),
        "Plugins" => typeof(PluginsPage),
        "Server" => typeof(ServerPage),
        "Settings" => typeof(SettingsPage),
        _ => typeof(DownloadsPage),
    };

    /// <summary>The first run is over: the normal window, on Downloads.</summary>
    public void FinishWelcome()
    {
        try
        {
            _app.FinishWelcome();
        }
        catch (SettingsException e)
        {
            CrashLog.Write("saving that the welcome is done", e); // shown again next time; nothing else is lost
        }
        WelcomeFrame.Visibility = Visibility.Collapsed;
        WelcomeFrame.Content = null;
        ShowShell(true);
        ShowDownloads();
    }

    private void ShowWelcome()
    {
        ShowShell(false);
        WelcomeFrame.Visibility = Visibility.Visible;
        WelcomeFrame.Navigate(typeof(WelcomePage));
    }

    /// <summary>The window's own parts: the sidebar, the pages and the command box, hidden while the first run shows.</summary>
    private void ShowShell(bool show)
    {
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        (Body.Visibility, CommandArea.Visibility) = (visibility, visibility);
        UpdateTitleBar();
    }

    /// <summary>To the box at the top, as Ctrl+K goes.</summary>
    public void FocusCommandBox() => CommandBox.Focus(FocusState.Keyboard);

    /// <summary>Text to fix and submit again (links Downloads couldn't add), in the command box.</summary>
    public void SetCommandText(string text) => CommandBox.Text = text;

    private void OnTitleBarSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleBar();

    private void OnTitleBarControlSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleBar();

    /// <summary>
    /// The title bar at the window's scale: room for the caption buttons, and the command box as the part that takes clicks
    /// (the rest of it drags the window).
    /// </summary>
    private void UpdateTitleBar()
    {
        if (AppTitleBar.XamlRoot is not { } root)
            return;
        var scale = root.RasterizationScale;
        CaptionColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
        var clickable = new FrameworkElement[] { CommandArea }
            .Where(element => element.Visibility == Visibility.Visible && element.ActualWidth > 0)
            .Select(element => PixelRect(element, scale))
            .ToArray();
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, clickable);
    }

    /// <summary>Where an element is in the window, in physical pixels.</summary>
    private static Windows.Graphics.RectInt32 PixelRect(FrameworkElement element, double scale)
    {
        var box = element.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return new Windows.Graphics.RectInt32((int)Math.Round(box.X * scale), (int)Math.Round(box.Y * scale),
                             (int)Math.Round(box.Width * scale), (int)Math.Round(box.Height * scale));
    }

    private void ApplyTheme()
    {
        var theme = _app.Settings.Text(SettingKey.Theme) switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        // a new accent only reaches what is already drawn when the theme changes: over and back, unseen
        if (Accent.Apply(_app.Settings))
            Root.RequestedTheme = Root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        Root.RequestedTheme = theme;
        UpdateCaptionButtons();
        UpdateModeLine();
    }

    /// <summary>What the window itself draws in colours picked by code.</summary>
    private void Repaint()
    {
        UpdateCaptionButtons();
        UpdateModeLine();
        UpdateProgress();
    }

    /// <summary>The caption buttons follow the window's theme, not only the system's.</summary>
    private void UpdateCaptionButtons()
    {
        var dark = Root.ActualTheme == ElementTheme.Dark;
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x80, 0, 0, 0);
        bar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x0A, 0, 0, 0);
        bar.ButtonHoverForegroundColor = bar.ButtonForegroundColor;
    }
}

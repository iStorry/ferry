using Ferry.App.Services;
using Ferry.App.ViewModels;
using Ferry.Core.Settings;
using Ferry.Plugins.Server;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

namespace Ferry.App.Pages;

/// <summary>
/// Settings (docs/mocks/settings.html), a section at a time, drawn from the SettingKey table so no list of settings is written
/// twice. What belongs to a plugin stays on its page. Values set in the environment are shown read-only.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private const string Downloads = "Downloads";
    private const string Schedule = "Schedule";
    private const string Adding = "Adding";
    private const string Appearance = "Appearance";
    private const string Behaviour = "Behaviour";
    private const string Lock = "Lock";
    private const string Advanced = "Advanced";

    private const string ModeNotSaved = "The choice wasn't saved";
    private const int MostFound = 8;
    private const double StripHours = 24;

    // every section as wide as the others, up to this
    private const double ContentWidth = 860;

    // narrower than this, the sections go across the top
    private const double SubnavWidth = 720;

    private static readonly SettingKey[] FastKeys = [SettingKey.FilesAtOnce, SettingKey.DownloadProfile, SettingKey.DownloadThreads, SettingKey.SpeedLimit];
    private static readonly SettingKey[] LandKeys = [SettingKey.SortByType, SettingKey.OnExisting];
    private static readonly SettingKey[] ScheduleKeys = [SettingKey.ScheduleOn, SettingKey.ScheduleStart, SettingKey.ScheduleStop];
    private static readonly SettingKey[] AddingKeys = [SettingKey.AskBeforeAdding, SettingKey.WatchClipboard];
    private static readonly SettingKey[] LookKeys = [SettingKey.Theme, SettingKey.Accent];
    private static readonly SettingKey[] BehaviourKeys = [SettingKey.CloseToTray, SettingKey.NotifyWhenDone, SettingKey.CheckUpdates];

    // the rows' words, where the table's help is written for the command line
    private static readonly Dictionary<string, string> RowHelp = new()
    {
        [SettingKey.FilesAtOnce.Name] = "How many files download at the same time.",
        [SettingKey.DownloadProfile.Name] = "How many connections to open to a site.",
        [SettingKey.DownloadThreads.Name] = "Overrides the profile; up to 16.",
        [SettingKey.SpeedLimit.Name] = "For all downloads together. Such as 10M or 500K; 0 for none.",
        [SettingKey.OnExisting.Name] = "For a file of the same name in the folder.",
        [SettingKey.CheckUpdates.Name] = "Once a day; you choose when to install.",
        [SettingKey.WatchClipboard.Name] = "Offers copied links when you copy them.",
        [SettingKey.Accent.Name] = "Bars, graphs and what is live. Everything else stays in ink.",
    };

    private readonly AppServices _app = App.Services;
    private readonly SettingRows _rows;
    private readonly Border _day = new() { Style = (Style)Application.Current.Resources["CardStyle"], Margin = new Thickness(0, 12, 0, 0) };
    private string _section = Downloads;

    public SettingsPage()
    {
        InitializeComponent();
        _rows = new SettingRows(_app, (title, message) => (Problem.Title, Problem.Message, Problem.IsOpen) = (title, message, true), Build);
        Loaded += (_, _) =>
        {
            _app.SettingsChanged += OnSettingsChanged;
            Build();
        };
        Unloaded += (_, _) => _app.SettingsChanged -= OnSettingsChanged;
    }

    /// <summary>The day strip says what the schedule's own rows have just changed; they stay as they are, with the focus in them.</summary>
    private void OnSettingsChanged()
    {
        if (_section == Schedule)
            ShowDay();
    }

    /// <summary>What had the focus has been drawn again: it stays on this page, not in the box at the top.</summary>
    private void KeepFocusHere() =>
        SubnavItems.Children.OfType<RadioButton>().FirstOrDefault(item => item.IsChecked == true)?.Focus(FocusState.Programmatic);

    private FerrySettings Settings => _app.Settings;

    /// <summary>The settings a search finds, each with the section it is in. A plugin's are found on its own page.</summary>
    public static IEnumerable<(SettingKey Key, string Section)> Find(string words) =>
        SettingKey.Standard.Where(key => key != SettingKey.DownloadDir
                && (key.Title.Contains(words, StringComparison.OrdinalIgnoreCase) || key.Help.Contains(words, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(key => key.Title.Contains(words, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(key => (key, SectionOf(key)));

    private static string SectionOf(SettingKey key) =>
        FastKeys.Contains(key) || LandKeys.Contains(key) ? Downloads
        : ScheduleKeys.Contains(key) ? Schedule
        : AddingKeys.Contains(key) ? Adding
        : LookKeys.Contains(key) ? Appearance
        : BehaviourKeys.Contains(key) ? Behaviour
        : Advanced;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string section)
            _section = section;
    }

    private void Build()
    {
        _rows.Building = true;
        foreach (var item in SubnavItems.Children.OfType<RadioButton>())
            item.IsChecked = (string)item.Tag == _section;
        Section.Children.Clear();
        switch (_section)
        {
            case Schedule:
                BuildSchedule();
                break;
            case Adding:
                Section.Children.Add(Heading("Adding links"));
                Section.Children.Add(Group(AddingKeys));
                break;
            case Appearance:
                BuildAppearance();
                break;
            case Behaviour:
                Section.Children.Add(Heading("Behaviour"));
                Section.Children.Add(Group(BehaviourKeys));
                break;
            case Lock:
                Section.Children.Add(Heading("App lock"));
                var lockRows = new StackPanel();
                AddLockCards(lockRows);
                Section.Children.Add(SettingRows.Group(lockRows));
                break;
            case Advanced:
                BuildAdvanced();
                break;
            default:
                BuildDownloads();
                break;
        }
        var problems = Settings.Problems;
        if (problems.Count > 0)
        {
            Problem.Title = "Some settings couldn't be used";
            Problem.Message = string.Join(" ", problems.Select(p => p.Message));
            Problem.IsOpen = true;
        }
        _rows.Building = false;
    }

    private void BuildDownloads()
    {
        Section.Children.Add(WhereCard());
        Section.Children.Add(SubHeading("How fast"));
        Section.Children.Add(Group(FastKeys));
        Section.Children.Add(SubHeading("Where they land"));
        Section.Children.Add(Group(LandKeys));
    }

    private void BuildSchedule()
    {
        Section.Children.Add(Heading("Schedule"));
        Section.Children.Add(Group([SettingKey.ScheduleOn]));
        Section.Children.Add(_day);
        ShowDay();
        var hours = Group([SettingKey.ScheduleStart, SettingKey.ScheduleStop]);
        hours.Margin = new Thickness(0, 12, 0, 0);
        Section.Children.Add(hours);
    }

    private void ShowDay()
    {
        var schedule = Settings.Schedule;
        _day.Child = schedule is null ? null : DayStrip(schedule);
        _day.Visibility = schedule is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>A day as a strip, with the hours downloads may run filled, now marked, and what that means in words.</summary>
    private StackPanel DayStrip(Core.Queue.DownloadSchedule schedule)
    {
        var now = TimeOnly.FromDateTime(DateTime.Now);
        var strip = new Grid { Height = 34, CornerRadius = new CornerRadius(8), Background = ThemeBrushes.Get(ThemeBrushes.Subtle, ActualTheme) };
        var marks = new Canvas { Height = 34 };
        strip.Children.Add(marks);
        var accent = ((SolidColorBrush)ThemeBrushes.Get(ThemeBrushes.Accent, ActualTheme)).Color;
        var fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x42, accent.R, accent.G, accent.B));
        strip.SizeChanged += (_, e) =>
        {
            marks.Children.Clear();
            double Position(TimeOnly time) => time.ToTimeSpan().TotalHours / StripHours * e.NewSize.Width;
            void Band(double from, double to)
            {
                var band = new Rectangle { Width = Math.Max(0, to - from), Height = 34, Fill = fill };
                Canvas.SetLeft(band, from);
                marks.Children.Add(band);
            }
            var (start, stop) = (Position(schedule.Start), Position(schedule.Stop));
            if (start <= stop)
            {
                Band(start, stop);
            }
            else
            {
                // across midnight: from the start to the day's end, and from its start to the stop
                Band(start, e.NewSize.Width);
                Band(0, stop);
            }
            var line = new Rectangle { Width = 2, Height = 34, Fill = ThemeBrushes.Get(ThemeBrushes.Primary, ActualTheme) };
            Canvas.SetLeft(line, Position(now));
            marks.Children.Add(line);
        };
        var labels = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (text, alignment) in new[] { ("00", HorizontalAlignment.Left), ("12", HorizontalAlignment.Center), ("24", HorizontalAlignment.Right) })
            labels.Children.Add(new TextBlock { Text = text, Style = (Style)Application.Current.Resources["CaptionFaintStyle"], HorizontalAlignment = alignment });
        var say = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrushes.Get(ThemeBrushes.Secondary, ActualTheme),
            Text = schedule.IsOpen(now)
                ? $"Now {now:HH:mm}. Files download until {schedule.Stop:HH:mm}, then pause; what they downloaded is kept."
                : $"Now {now:HH:mm}. Files wait until {schedule.Start:HH:mm} and pause at {schedule.Stop:HH:mm}; what they downloaded is kept.",
        };
        var panel = new StackPanel();
        panel.Children.Add(strip);
        panel.Children.Add(labels);
        panel.Children.Add(say);
        return panel;
    }

    private void BuildAppearance()
    {
        Section.Children.Add(Heading("Appearance"));
        Section.Children.Add(SubHeading("Theme"));
        Section.Children.Add(ThemeCards());
        Section.Children.Add(SubHeading("Accent"));
        var rows = new StackPanel();
        rows.Children.Add(SettingRows.Card("Colour for what is moving", Describe(SettingKey.Accent), AccentSwatches()));
        Section.Children.Add(SettingRows.Group(rows));
    }

    /// <summary>As Windows, light and dark, each a small picture of the window.</summary>
    private Grid ThemeCards()
    {
        var cards = new Grid { ColumnSpacing = 10 };
        var locked = Settings[SettingKey.Theme].Source == SettingSource.Environment;
        var choices = new (string Value, string Label, Windows.UI.Color Back, Windows.UI.Color Card, Windows.UI.Color? Half)[]
        {
            ("System", "As Windows", Windows.UI.Color.FromArgb(0xFF, 0xEE, 0xF0, 0xF2), Windows.UI.Color.FromArgb(0x52, 0x80, 0x8A, 0x96), Windows.UI.Color.FromArgb(0xFF, 0x0F, 0x12, 0x16)),
            ("Light", "Light", Windows.UI.Color.FromArgb(0xFF, 0xEE, 0xF0, 0xF2), Colors.White, null),
            ("Dark", "Dark", Windows.UI.Color.FromArgb(0xFF, 0x0F, 0x12, 0x16), Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x1F, 0x25), null),
        };
        foreach (var (choice, at) in choices.Select((choice, at) => (choice, at)))
        {
            cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var look = new Grid { Height = 72, CornerRadius = new CornerRadius(7), Padding = new Thickness(6), ColumnSpacing = 4, Background = new SolidColorBrush(choice.Back) };
            if (choice.Half is { } half)
            {
                look.Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 1),
                    GradientStops = { new GradientStop { Color = choice.Back, Offset = 0.5 }, new GradientStop { Color = half, Offset = 0.5 } },
                };
            }
            look.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            look.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            var (side, page) = (new Border { CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(choice.Card) }, new Border { CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(choice.Card) });
            Grid.SetColumn(page, 1);
            look.Children.Add(side);
            look.Children.Add(page);
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(look);
            content.Children.Add(new TextBlock { Text = choice.Label });
            var chosen = Settings.Text(SettingKey.Theme) == choice.Value;
            var card = new Button
            {
                Content = content,
                Padding = new Thickness(8, 8, 8, 10),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(12),
                Background = ThemeBrushes.Get(ThemeBrushes.Card, ActualTheme),
                BorderThickness = new Thickness(chosen ? 2 : 1),
                BorderBrush = ThemeBrushes.Get(chosen ? ThemeBrushes.Primary : ThemeBrushes.CardStroke, ActualTheme),
                IsEnabled = !locked,
            };
            if (chosen)
            {
                // pointed at, it is still the one chosen
                card.Resources["ButtonBorderBrushPointerOver"] = card.BorderBrush;
                card.Resources["ButtonBorderBrushPressed"] = card.BorderBrush;
            }
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, $"Theme: {choice.Label}");
            card.Click += (_, _) => Save(SettingKey.Theme, choice.Value);
            Grid.SetColumn(card, at);
            cards.Children.Add(card);
        }
        return cards;
    }

    private StackPanel AccentSwatches()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var locked = Settings[SettingKey.Accent].Source == SettingSource.Environment;
        var dark = ActualTheme == ElementTheme.Dark;
        foreach (var name in SettingKey.Accent.Choices)
        {
            var chosen = Settings.Text(SettingKey.Accent) == name;
            var dot = new Button
            {
                Width = 24,
                Height = 24,
                MinWidth = 0,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                IsEnabled = !locked,
                // its own, not the button's: pointed at, a button draws its background and border in the theme's colours
                Content = new Ellipse
                {
                    Width = 24,
                    Height = 24,
                    Fill = new SolidColorBrush(Accent.ColorOf(name, dark)),
                    Stroke = ThemeBrushes.Get(ThemeBrushes.Primary, ActualTheme),
                    StrokeThickness = chosen ? 2 : 0,
                },
            };
            var label = name == Accent.AsWindows ? "As Windows" : name;
            ToolTipService.SetToolTip(dot, label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dot, $"Accent: {label}");
            dot.Click += (_, _) => Save(SettingKey.Accent, name);
            row.Children.Add(dot);
        }
        return row;
    }

    private void BuildAdvanced()
    {
        Section.Children.Add(Heading("Advanced"));
        var shown = FastKeys.Concat(LandKeys).Concat(ScheduleKeys).Concat(AddingKeys).Concat(LookKeys).Concat(BehaviourKeys).Append(SettingKey.DownloadDir);
        var rows = new StackPanel();
        foreach (var key in SettingKey.Standard.Except(shown))
            rows.Children.Add(_rows.AdvancedRow(key));
        Section.Children.Add(SettingRows.Group(rows));
    }

    /// <summary>Where downloads happen, the first and biggest thing here.</summary>
    private Border WhereCard()
    {
        var server = Settings.Mode == DownloadMode.Server;
        var folder = Settings.DownloadFolder;
        var words = new StackPanel { Spacing = 2 };
        words.Children.Add(new TextBlock { Text = "Where downloads happen", Style = (Style)Application.Current.Resources["CaptionMutedStyle"] });
        words.Children.Add(new TextBlock
        {
            Text = server ? $"On {ServerProfile.From(Settings).Display}, then here" : _app.Queue.Where == DownloadPlace.RemoteQueue ? $"On {_app.Queue.WhereDisplay}" : "On this PC",
            Style = (Style)Application.Current.Resources["SubheadingStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });
        var path = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        path.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        path.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        path.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        path.Children.Add(new FontIcon { Glyph = "\uE838", FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeBrushes.Get(ThemeBrushes.Secondary, ActualTheme) });
        var shownFolder = new TextBlock { Text = folder, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = ThemeBrushes.Get(ThemeBrushes.Secondary, ActualTheme) };
        ToolTipService.SetToolTip(shownFolder, folder);
        Grid.SetColumn(shownFolder, 1);
        path.Children.Add(shownFolder);
        var change = _rows.Control(SettingKey.DownloadDir);
        if (change is Button button)
            button.Style = (Style)Application.Current.Resources["SubtleButtonStyle"];
        Grid.SetColumn(change, 2);
        path.Children.Add(change);
        words.Children.Add(path);
        var layout = new Grid { ColumnSpacing = 16 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(words);
        if (Settings.ServerSupportIsOn && ServerProfile.From(Settings).IsSet)
        {
            var choice = SettingRows.ModeChoice(server, Settings[ServerKeys.Mode].Source == SettingSource.Environment, OnModeChosen);
            Grid.SetColumn(choice, 1);
            layout.Children.Add(choice);
        }
        return new Border { Style = (Style)Application.Current.Resources["CardStyle"], Padding = new Thickness(18, 16, 18, 16), Child = layout };
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
            (Problem.Title, Problem.Message, Problem.IsOpen) = (ModeNotSaved, problem, true);
        Build(); // the choice as it is, also when the dialog was cancelled
    }

    private Border Group(IEnumerable<SettingKey> keys)
    {
        var rows = new StackPanel();
        foreach (var key in keys)
            rows.Children.Add(SettingRows.Card(key.Title, Describe(key), _rows.Control(key)));
        return SettingRows.Group(rows);
    }

    private string Describe(SettingKey key)
    {
        var help = RowHelp.GetValueOrDefault(key.Name, key.Help);
        return Settings[key].Source == SettingSource.Environment ? $"{help} Set by {key.Name} in the environment, so it can't be changed here." : help;
    }

    private void Save(SettingKey key, string value)
    {
        try
        {
            _app.Change(store => store.Set(key.Name, value));
        }
        catch (SettingsException e)
        {
            (Problem.Title, Problem.Message, Problem.IsOpen) = ($"{key.Title} wasn't changed", SettingRows.Why(key, e), true);
        }
        Build();
        KeepFocusHere();
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["SubheadingStyle"],
        Margin = new Thickness(0, 0, 0, 14),
    };

    private static TextBlock SubHeading(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["SectionHeaderStyle"],
        Margin = new Thickness(0, 24, 0, 8),
    };

    private void OnSubnav(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string section)
            ShowSection(section);
    }

    /// <summary>A section of its own: what went wrong in the last one stays behind with it.</summary>
    private void ShowSection(string section)
    {
        _section = section;
        Problem.IsOpen = false;
        ContentScroller.ChangeView(null, 0, null, disableAnimation: true);
        Build();
    }

    private void OnOpenPlugins(object sender, RoutedEventArgs e) => App.Window?.ShowPlugins();

    private void OnOpenAbout(object sender, RoutedEventArgs e) => App.Window?.ShowAbout();

    private void OnFindChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            FindBox.ItemsSource = FindBox.Text.Trim() is { Length: > 0 } words ? Find(words).Take(MostFound).Select(found => new FoundSetting(found.Key, found.Section)).ToList() : null;
    }

    private void OnFindSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var found = args.ChosenSuggestion as FoundSetting
            ?? (args.QueryText.Trim() is { Length: > 0 } words && Find(words).Select(hit => new FoundSetting(hit.Key, hit.Section)).FirstOrDefault() is { } first ? first : null);
        if (found is null)
            return;
        sender.Text = "";
        sender.ItemsSource = null;
        ShowSection(found.Section);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var across = e.NewSize.Width < SubnavWidth;
        ContentArea.Width = Math.Min(ContentWidth, e.NewSize.Width - (across ? 0 : 208));
        ContentArea.Padding = (Thickness)Application.Current.Resources[across ? "NarrowPagePadding" : "PagePadding"];
        SubnavColumn.Width = across ? new GridLength(0) : new GridLength(208);
        SubnavRow.Height = across ? GridLength.Auto : new GridLength(0);
        Grid.SetColumn(SubnavPanel, 0);
        Grid.SetColumnSpan(SubnavPanel, across ? 2 : 1);
        Grid.SetRowSpan(SubnavPanel, across ? 1 : 2);
        Grid.SetRow(ContentScroller, across ? 1 : 0);
        Grid.SetRowSpan(ContentScroller, across ? 1 : 2);
        SubnavPanel.BorderThickness = across ? new Thickness(0, 0, 0, 1) : new Thickness(0, 0, 1, 0);
        SubnavScroller.HorizontalScrollMode = across ? ScrollMode.Enabled : ScrollMode.Disabled;
        SubnavScroller.HorizontalScrollBarVisibility = across ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
        SubnavScroller.VerticalScrollMode = across ? ScrollMode.Disabled : ScrollMode.Enabled;
        SubnavItems.Orientation = across ? Orientation.Horizontal : Orientation.Vertical;
        SubnavBody.Padding = across ? new Thickness(8) : new Thickness(12, 24, 12, 12);
        var hidden = across ? Visibility.Collapsed : Visibility.Visible;
        (SubnavTitle.Visibility, FindBox.Visibility, SubnavLine.Visibility, SubnavLinks.Visibility) = (hidden, hidden, hidden, hidden);
    }

    /// <summary>What the find box lists; a screen reader and the box both read its words.</summary>
    private sealed record FoundSetting(SettingKey Key, string Section)
    {
        public override string ToString() => Key.Title;
    }
}

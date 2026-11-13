using System.Globalization;
using Ferry.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace Ferry.App.Services;

/// <summary>
/// A setting as a row, with the control its type calls for, saved as it changes: what Settings and a plugin's page both draw.
/// Rows go in one card with lines between (<see cref="Group"/>). A value set in the environment is shown read-only.
/// </summary>
/// <param name="report">told what wasn't saved and why: a title and a message</param>
/// <param name="rebuild">a change needs the page drawn again</param>
public sealed class SettingRows(AppServices app, Action<string, string> report, Action rebuild)
{
    private static readonly Dictionary<string, string> ChoiceLabels = new()
    {
        ["conservative"] = "Conservative · 2 connections",
        ["balanced"] = "Balanced · 4 connections",
        ["aggressive"] = "Aggressive · 8 connections",
        ["rename"] = "Keep both: name (1).ext",
        ["skip"] = "Skip it",
        ["overwrite"] = "Replace it",
        ["System"] = "As Windows",
    };

    /// <summary>The page is drawing its controls: what they say as they are set isn't a change.</summary>
    public bool Building { get; set; }

    /// <summary>One thing with its words and its control, as a row of a <see cref="Group"/>.</summary>
    public static Border Card(object title, string description, FrameworkElement? control = null)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title as FrameworkElement ?? new TextBlock { Text = (string)title, TextWrapping = TextWrapping.Wrap });
        if (description.Length > 0)
            text.Children.Add(new TextBlock { Text = description, Style = (Style)Application.Current.Resources["CaptionMutedStyle"] });
        grid.Children.Add(text);
        if (control is not null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            control.HorizontalAlignment = HorizontalAlignment.Right;
            // what a button says is its name already; an input has only the card's title to go by
            if (title is string name && control is not Button && Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(control).Length == 0)
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, name); // what a screen reader says for it
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }
        return new Border { Style = (Style)Application.Current.Resources["SetRowStyle"], Child = grid };
    }

    /// <summary>These rows in one card, with lines between. <paramref name="rows"/> stays theirs, so a row can still be replaced in it.</summary>
    public static Border Group(StackPanel rows)
    {
        MarkLines(rows);
        return new Border { Style = (Style)Application.Current.Resources["SetCardStyle"], Child = rows };
    }

    /// <summary>Each row but the first has a line above; also after rows have changed.</summary>
    public static void MarkLines(Panel rows)
    {
        foreach (var (row, at) in rows.Children.OfType<Border>().Select((row, at) => (row, at)))
            row.BorderThickness = at == 0 ? new Thickness(0) : new Thickness(0, 1, 0, 0);
    }

    /// <summary>This PC or your server, as a segmented control.</summary>
    /// <param name="chosen">told whether the server was chosen</param>
    public static Border ModeChoice(bool server, bool locked, Action<bool> chosen)
    {
        var group = $"Mode{Guid.NewGuid():N}";
        var style = (Style)Application.Current.Resources["SegmentItemStyle"];
        RadioButton Item(string glyph, string text, bool isServer)
        {
            var words = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            words.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
            words.Children.Add(new TextBlock { Text = text });
            var item = new RadioButton { Style = style, GroupName = group, Content = words, IsChecked = server == isServer, IsEnabled = !locked };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, text);
            item.Click += (_, _) => chosen(isServer);
            return item;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        row.Children.Add(Item(Glyphs.Pc, "This PC", false));
        row.Children.Add(Item(Glyphs.Server, "Your server", true));
        return new Border { Style = (Style)Application.Current.Resources["SegmentsStyle"], Child = row, VerticalAlignment = VerticalAlignment.Center };
    }

    public Border AdvancedRow(SettingKey key)
    {
        var grid = new Grid { ColumnSpacing = 12, MinHeight = 36 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = key.Title, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock
        {
            Text = app.Settings[key].Source == SettingSource.Environment ? $"{key.Name} · set in the environment" : key.Name,
            Style = (Style)Application.Current.Resources["CaptionMutedStyle"],
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11,
        });
        ToolTipService.SetToolTip(text, key.Help);
        grid.Children.Add(text);
        var control = Control(key);
        control.VerticalAlignment = VerticalAlignment.Center;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, key.Title);
        if (control is TextBox box)
            box.Width = 240;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        var reset = new Button
        {
            Content = "Reset",
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = app.Settings[key].Source == SettingSource.File,
        };
        reset.Click += (_, _) =>
        {
            Unset(key);
            rebuild();
        };
        Grid.SetColumn(reset, 2);
        grid.Children.Add(reset);
        return new Border { Child = grid, Style = (Style)Application.Current.Resources["SetRowStyle"] };
    }

    public FrameworkElement Control(SettingKey key)
    {
        var value = app.Settings[key];
        var locked = value.Source == SettingSource.Environment;
        FrameworkElement control = key.Type switch
        {
            SettingType.Choice => Choice(key, value.Text),
            SettingType.Boolean => Toggle(key, value.Text),
            SettingType.WholeNumber when key == SettingKey.DownloadThreads => Threads(value.Text),
            SettingType.WholeNumber => Number(key, value.Text),
            SettingType.Folder => Folder(key),
            _ => Text(key, value.Text),
        };
        if (locked && control is Control input)
            input.IsEnabled = false;
        return control;
    }

    private ComboBox Choice(SettingKey key, string current)
    {
        var box = new ComboBox { MinWidth = 220 };
        foreach (var choice in key.Choices)
            box.Items.Add(new ComboBoxItem { Content = ChoiceLabels.GetValueOrDefault(choice, choice), Tag = choice });
        box.SelectedIndex = key.Choices.ToList().IndexOf(current);
        box.SelectionChanged += (_, _) =>
        {
            if (!Building && box.SelectedItem is ComboBoxItem { Tag: string choice })
                Set(key, choice);
        };
        return box;
    }

    private ComboBox Threads(string current)
    {
        var profile = DownloadOptions(app.Settings.Profile);
        var box = new ComboBox { MinWidth = 220 };
        int[] values = [0, 1, 2, 4, 6, 8, 12, 16];
        foreach (var n in values)
            box.Items.Add(new ComboBoxItem { Content = n == 0 ? $"From the profile ({profile})" : n.ToString(CultureInfo.InvariantCulture), Tag = n });
        box.SelectedIndex = Math.Max(0, Array.IndexOf(values, int.Parse(current, CultureInfo.InvariantCulture)));
        box.SelectionChanged += (_, _) =>
        {
            if (!Building && box.SelectedItem is ComboBoxItem { Tag: int n })
                Set(SettingKey.DownloadThreads, n.ToString(CultureInfo.InvariantCulture));
        };
        return box;
    }

    private static int DownloadOptions(Core.Download.DownloadProfile profile) =>
        Core.Download.DownloadOptions.For(profile).Connections;

    private LabeledSwitch Toggle(SettingKey key, string current)
    {
        var toggle = new LabeledSwitch { IsOn = current == "true", AccessibleName = key.Title };
        toggle.Toggled += (_, _) =>
        {
            if (!Building)
                Set(key, toggle.IsOn ? "true" : "false");
        };
        return toggle;
    }

    private NumberBox Number(SettingKey key, string current)
    {
        var box = new NumberBox
        {
            Value = double.Parse(current, CultureInfo.InvariantCulture),
            Minimum = key.Min,
            Maximum = Math.Min(key.Max, int.MaxValue),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            SmallChange = 1,
            MinWidth = 120,
        };
        box.ValueChanged += (_, args) =>
        {
            if (!Building && !double.IsNaN(args.NewValue))
                Set(key, ((long)args.NewValue).ToString(CultureInfo.InvariantCulture));
        };
        return box;
    }

    private TextBox Text(SettingKey key, string current)
    {
        var box = new TextBox { Text = current, MinWidth = 160 };
        void Commit()
        {
            if (!Building && box.Text != app.Settings[key].Text && !Set(key, box.Text))
                box.Text = app.Settings[key].Text;
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
                Commit();
        };
        return box;
    }

    private Button Folder(SettingKey key)
    {
        var button = new Button { Content = "Change…" };
        button.Click += async (_, _) =>
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Window!.Handle);
            if (await picker.PickSingleFolderAsync() is { } folder && Set(key, folder.Path))
                rebuild();
        };
        return button;
    }

    /// <summary>Save a setting; a value it can't take is shown, and false returned.</summary>
    private bool Set(SettingKey key, string value)
    {
        try
        {
            app.Change(store => store.Set(key.Name, value));
            return true;
        }
        catch (SettingsException e)
        {
            report($"{key.Title} wasn't changed", Why(key, e));
            return false;
        }
    }

    /// <summary>Why a value was refused, without the setting's name in the environment, which the page has no use for.</summary>
    public static string Why(SettingKey key, SettingsException e) => e.Message.Replace($"{key.Name}: ", "", StringComparison.Ordinal);

    private void Unset(SettingKey key)
    {
        try
        {
            app.Change(store => store.Unset(key.Name));
        }
        catch (SettingsException e)
        {
            report($"{key.Title} wasn't reset", e.Message);
        }
    }
}

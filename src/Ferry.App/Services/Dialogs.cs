using Ferry.Core.AppLocking;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Ferry.App.Services;

/// <summary>What a dialog is about: its icon's glyph and colour say so before its words do.</summary>
public enum DialogTone
{
    Question,

    /// <summary>Something will stop or be lost unless the user says otherwise.</summary>
    Caution,

    /// <summary>Something can't be undone (forgetting a server, removing what was downloaded).</summary>
    Danger,

    Error,
}

/// <summary>
/// Content dialogs (docs/mocks/dialogs.html): an icon for what they're about beside their words, and the safe
/// choice as the default button. One shows at a time (WinUI allows no more): a second waits for the first to close. A
/// page that is no longer shown (no XamlRoot) uses the window's.
/// </summary>
public static class Dialogs
{
    private const double IconTile = 36;
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>Ask a question with two answers; true for <paramref name="primary"/>, false if it can't be asked.</summary>
    public static async Task<bool> ConfirmAsync(XamlRoot? root, string title, object content, string primary, string close,
                                                bool primaryIsDefault = true, DialogTone tone = DialogTone.Question)
    {
        if (Make(root, title, content, tone) is not { } dialog)
            return false;
        dialog.PrimaryButtonText = primary;
        dialog.CloseButtonText = close;
        dialog.DefaultButton = primaryIsDefault ? ContentDialogButton.Primary : ContentDialogButton.Close;
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>Ask for a line of text; null when cancelled (or it can't be asked).</summary>
    public static async Task<string?> AskAsync(XamlRoot? root, string title, string label, string value, string primary)
    {
        var box = new TextBox { Header = label, Text = value };
        box.Loaded += (_, _) => box.SelectAll();
        if (Make(root, title, box, DialogTone.Question) is not { } dialog)
            return null;
        dialog.PrimaryButtonText = primary;
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        return await ShowAsync(dialog) == ContentDialogResult.Primary ? box.Text : null;
    }

    /// <summary>
    /// Ask for PINs, a box for each label. The dialog stays open, saying what is wrong, until <paramref name="accept"/>
    /// takes them (it returns null) or the user cancels; false then, or if it can't be asked.
    /// </summary>
    public static async Task<bool> AskPinsAsync(XamlRoot? root, string title, string note, IReadOnlyList<string> labels, string primary,
                                                Func<string[], Task<string?>> accept)
    {
        var boxes = labels.Select(label => new PasswordBox
        {
            Header = label,
            MaxLength = AppLock.PinLength,
            InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.NumericPin) } },
        }).ToArray();
        var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap });
        foreach (var box in boxes)
            content.Children.Add(box);
        content.Children.Add(problem);
        if (Make(root, title, content, DialogTone.Question) is not { } dialog)
            return false;
        problem.Foreground = ThemeBrushes.Get(ThemeBrushes.Critical, dialog.RequestedTheme);
        dialog.PrimaryButtonText = primary;
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (await accept([.. boxes.Select(box => box.Password)]) is not { } wrong)
                    return;
                args.Cancel = true;
                (problem.Text, problem.Visibility) = (wrong, Visibility.Visible);
            }
            finally
            {
                deferral.Complete();
            }
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Ask for a choice between two ways to go on, with a way out; <paramref name="mayClose"/> keeps the dialog open
    /// (it shows what is wrong itself) while what was entered can't be used. <see cref="ContentDialogResult.None"/> when
    /// cancelled, or if it can't be asked.
    /// </summary>
    public static async Task<ContentDialogResult> ChooseAsync(XamlRoot? root, string title, object content, string primary, string secondary,
                                                              Func<bool> mayClose)
    {
        if (Make(root, title, content, DialogTone.Question) is not { } dialog)
            return ContentDialogResult.None;
        dialog.PrimaryButtonText = primary;
        dialog.SecondaryButtonText = secondary;
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = !mayClose();
        dialog.SecondaryButtonClick += (_, args) => args.Cancel = !mayClose();
        return await ShowAsync(dialog);
    }

    /// <summary>
    /// A form, with no icon (its fields say what it is about), whose buttons the caller sets up; show it with
    /// <see cref="ShowFormAsync"/>. Null if it can't be shown.
    /// </summary>
    public static ContentDialog? Form(XamlRoot? root, string title, UIElement content)
    {
        root ??= App.Window?.Content?.XamlRoot;
        if (root is null)
            return null; // the window is gone
        return new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = content,
            Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
            RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
        };
    }

    /// <summary>Show a dialog from <see cref="Form"/>, after any that is open.</summary>
    public static Task<ContentDialogResult> ShowFormAsync(ContentDialog dialog) => ShowAsync(dialog);

    /// <summary>
    /// Say what is about to happen, counting down, with a way out; true when the time ran out, false when the user
    /// stopped it (or it can't be asked).
    /// </summary>
    /// <param name="words">the line under the title, given the seconds left</param>
    public static async Task<bool> CountdownAsync(XamlRoot? root, string title, Func<int, string> words, int seconds)
    {
        var line = new TextBlock { Text = words(seconds), TextWrapping = TextWrapping.Wrap };
        if (Make(root, title, line, DialogTone.Caution) is not { } dialog)
            return false;
        dialog.CloseButtonText = "Stop";
        dialog.DefaultButton = ContentDialogButton.Close;
        var left = seconds;
        var ranOut = false;
        var timer = dialog.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) =>
        {
            line.Text = words(--left);
            if (left > 0)
                return;
            ranOut = true;
            dialog.Hide();
        };
        dialog.Opened += (_, _) => timer.Start();
        dialog.Closed += (_, _) => timer.Stop();
        await ShowAsync(dialog);
        return ranOut;
    }

    /// <summary>Tell the user something went wrong.</summary>
    public static async Task ShowErrorAsync(XamlRoot? root, string title, string message)
    {
        if (Make(root, title, message, DialogTone.Error) is not { } dialog)
            return;
        dialog.CloseButtonText = "OK";
        dialog.DefaultButton = ContentDialogButton.Close;
        await ShowAsync(dialog);
    }

    /// <summary>
    /// Run <paramref name="work"/> from an event handler: what it throws is logged and said to the user, under
    /// <paramref name="title"/>, instead of ending in the app's unhandled-exception handler with no word.
    /// </summary>
    public static async Task GuardAsync(XamlRoot? root, string title, Func<Task> work)
    {
        try
        {
            await work();
        }
#pragma warning disable CA1031 // what an event handler's work throws has nowhere else to go: this is the last stop before the app's own handler
        catch (Exception e)
#pragma warning restore CA1031
        {
            CrashLog.Write(title, e);
            await ShowErrorAsync(root, title, e.Message);
        }
    }

    private static ContentDialog? Make(XamlRoot? root, string title, object content, DialogTone tone)
    {
        root ??= App.Window?.Content?.XamlRoot;
        if (root is null)
            return null; // the window is gone
        var theme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
        var words = content is string text ? new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } : content;
        return new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = WithIcon(tone, words, theme),
            Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
            RequestedTheme = theme,
        };
    }

    /// <summary>The dialog's words with its icon beside them, on a tile, in the tone's colour.</summary>
    private static Grid WithIcon(DialogTone tone, object content, ElementTheme theme)
    {
        var (glyph, colour) = tone switch
        {
            DialogTone.Caution => ("\uE7BA", ThemeBrushes.Caution),
            DialogTone.Danger => ("\uE7BA", ThemeBrushes.Critical),
            DialogTone.Error => ("\uEA39", ThemeBrushes.Critical),
            _ => ("\uE897", ThemeBrushes.Secondary), // a question is in ink: nothing about it is moving or wrong
        };
        var tint = ThemeBrushes.Get(colour, theme);
        var icon = new Border
        {
            Width = IconTile,
            Height = IconTile,
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Top,
            Background = tone == DialogTone.Question ? ThemeBrushes.Get(ThemeBrushes.Subtle, theme) : Pill.Wash(tint),
            Child = new FontIcon { Glyph = glyph, FontSize = 18, Foreground = tint },
        };
        var words = new ContentPresenter { Content = content, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(words, 1);
        var layout = new Grid { ColumnSpacing = 16 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(icon);
        layout.Children.Add(words);
        return layout;
    }

    private static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await OneAtATime.WaitAsync();
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            OneAtATime.Release();
        }
    }
}

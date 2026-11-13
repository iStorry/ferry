using System.Globalization;
using Ferry.App.Services;
using Ferry.Core.AppLocking;
using Ferry.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace Ferry.App.Pages;

/// <summary>
/// The app lock's PIN (docs/mocks/lock.html). It is checked as the last digit goes in. A wrong one shakes the
/// dots, the same each time: nothing says how many tries are left, or that the decoy opened instead of the app.
/// </summary>
public sealed partial class LockPage : Page
{
    private readonly AppLock _lock = App.Services.Lock;
    private string _typed = "";
    private bool _checking;
    private bool _wrong;

    public LockPage()
    {
        InitializeComponent();
        AddKeys();
        for (var i = 0; i < AppLock.PinLength; i++)
            Dots.Children.Add(new Ellipse { Style = (Style)Resources["PinDotStyle"] });
        ShowDots();
        if (_lock.Status == LockStatus.Unreadable)
        {
            ShowProblem($"The app lock can't be read on this PC. To start again without it, remove {_lock.FilePath}.");
            Keypad.IsHitTestVisible = false;
            Keypad.Opacity = 0.4;
        }
        ActualThemeChanged += (_, _) => ShowDots();
        Loaded += (_, _) => Focus(FocusState.Programmatic);
    }

    /// <summary>1 to 9 in rows of three, then 0 in the middle under them, beside the delete key.</summary>
    private void AddKeys()
    {
        for (var digit = 0; digit <= 9; digit++)
        {
            var key = new Button { Content = digit.ToString(CultureInfo.InvariantCulture), Style = (Style)Resources["PinKeyStyle"] };
            var typed = (char)('0' + digit);
            key.Click += (_, _) => Type(typed);
            var (row, column) = digit == 0 ? (3, 1) : ((digit - 1) / 3, (digit - 1) % 3);
            Grid.SetRow(key, row);
            Grid.SetColumn(key, column);
            Keypad.Children.Add(key);
        }
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (Digit(e.Key) is { } digit)
            Type(digit);
        else if (e.Key == VirtualKey.Back)
            Delete();
        else
        {
            base.OnKeyDown(e);
            return;
        }
        e.Handled = true;
    }

    private static char? Digit(VirtualKey key) => key switch
    {
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => (char)('0' + (key - VirtualKey.Number0)),
        >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9 => (char)('0' + (key - VirtualKey.NumberPad0)),
        _ => null,
    };

    private void OnDeleteClick(object sender, RoutedEventArgs e) => Delete();

    private void Type(char digit)
    {
        if (_checking || _lock.Status != LockStatus.On || _typed.Length == AppLock.PinLength)
            return;
        WrongText.Opacity = 0;
        _typed += digit;
        ShowDots();
        if (_typed.Length == AppLock.PinLength)
            _ = CheckAsync(_typed);
    }

    private void Delete()
    {
        if (_checking || _typed.Length == 0)
            return;
        _typed = _typed[..^1];
        ShowDots();
    }

    private async Task CheckAsync(string pin)
    {
        _checking = true;
        PinCheck result;
        try
        {
            result = await Task.Run(() => _lock.Check(pin)); // the hash is slow on purpose
        }
        catch (IOException e)
        {
            CrashLog.Write("counting a PIN try", e);
            ShowProblem($"The PIN can't be checked: {e.Message}");
            Wrong();
            return;
        }
        if (result == PinCheck.Wrong)
        {
            Wrong();
            return;
        }
        try
        {
            App.Window?.Unlock(decoy: result == PinCheck.Decoy);
        }
        catch (Exception e) when (e is IOException or SettingsException)
        {
            CrashLog.Write("opening the decoy", e);
            Wrong(); // as any wrong PIN: the next ones count again
        }
    }

    /// <summary>Shake the dots, in coral; they clear, and take digits again, once it is over.</summary>
    private void Wrong()
    {
        _wrong = true;
        ShowDots();
        WrongText.Opacity = 1;
        AutomationName("Wrong PIN");
        Shake.Begin();
    }

    private void OnShakeCompleted(object sender, object e)
    {
        _typed = "";
        _checking = false;
        _wrong = false;
        ShowDots();
    }

    private void ShowDots()
    {
        var filled = ThemeBrushes.Get(_wrong ? ThemeBrushes.Critical : ThemeBrushes.Primary, ActualTheme);
        foreach (var (dot, i) in Dots.Children.OfType<Ellipse>().Select((dot, i) => (dot, i)))
        {
            var on = _wrong || i < _typed.Length;
            dot.Fill = on ? filled : null;
            dot.StrokeThickness = on ? 0 : 1.5; // an empty dot is its outline, from the style, which follows the theme
        }
        AutomationName($"{_typed.Length} of {AppLock.PinLength} digits");
    }

    private void AutomationName(string name) => Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Dots, name);

    private void ShowProblem(string text)
    {
        ProblemText.Text = text;
        ProblemCard.Visibility = Visibility.Visible;
    }
}

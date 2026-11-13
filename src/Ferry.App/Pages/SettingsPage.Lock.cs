using System.Globalization;
using Ferry.App.Services;
using Ferry.Core.AppLocking;
using Microsoft.UI.Xaml.Controls;

namespace Ferry.App.Pages;

/// <summary>Settings' app lock: the PIN asked for each time Ferry opens, and how many wrong ones open the decoy.</summary>
public sealed partial class SettingsPage
{
    private const string NotThePin = "That's not the PIN.";

    private void AddLockCards(Panel section)
    {
        var on = _app.Lock.Status == LockStatus.On;
        var toggle = new LabeledSwitch { IsOn = on, AccessibleName = "App lock" };
        toggle.Toggled += async (_, _) =>
        {
            if (_rows.Building)
                return;
            await (toggle.IsOn ? TurnLockOnAsync() : TurnLockOffAsync());
            Build(); // the switch as the lock is, also when the dialog was cancelled
        };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        if (on)
        {
            var change = new Button { Content = "Change PIN…" };
            change.Click += async (_, _) => await ChangePinAsync();
            controls.Children.Add(change);
        }
        controls.Children.Add(toggle);
        section.Children.Add(SettingRows.Card("Lock Ferry with a PIN",
            $"A {AppLock.PinLength}-digit PIN each time Ferry opens. Nothing loads or downloads until it is in.", controls));
        if (on)
            section.Children.Add(SettingRows.Card("After wrong PINs",
                "Open Ferry empty, as a new install, after this many in a row; your downloads, albums and server stay hidden.",
                DecoyChoice()));
    }

    private ComboBox DecoyChoice()
    {
        var box = new ComboBox { MinWidth = 220 };
        foreach (var n in AppLock.DecoyChoices)
            box.Items.Add(new ComboBoxItem { Content = n == AppLock.DecoyOff ? "Never" : $"{n.ToString(CultureInfo.InvariantCulture)} in a row", Tag = n });
        box.SelectedIndex = AppLock.DecoyChoices.ToList().IndexOf(_app.Lock.DecoyAfter);
        box.SelectionChanged += (_, _) =>
        {
            if (_rows.Building || box.SelectedItem is not ComboBoxItem { Tag: int n })
                return;
            try
            {
                _app.Lock.SetDecoyAfter(n);
            }
            catch (IOException e)
            {
                (Problem.Title, Problem.Message, Problem.IsOpen) = ("The choice wasn't saved", e.Message, true);
            }
        };
        return box;
    }

    private Task<bool> TurnLockOnAsync() =>
        Dialogs.AskPinsAsync(XamlRoot, "Lock Ferry with a PIN",
            $"Ferry asks for it each time it opens. If you forget it, removing {_app.Lock.FilePath} turns the lock off.",
            ["PIN", "PIN again"], "Turn on", async pins =>
            {
                if (NewPinProblem(pins[0], pins[1]) is { } problem)
                    return problem;
                return await SaveLockAsync(() => _app.Lock.TurnOn(pins[0], AppLock.DefaultDecoyAfter));
            });

    private Task<bool> TurnLockOffAsync() =>
        Dialogs.AskPinsAsync(XamlRoot, "Turn the app lock off?", "Ferry will open without asking for a PIN.",
            ["PIN"], "Turn off", pins => SaveLockAsync(() => _app.Lock.TurnOff(pins[0]) ? null : NotThePin));

    private Task<bool> ChangePinAsync() =>
        Dialogs.AskPinsAsync(XamlRoot, "Change the PIN", "The new PIN opens Ferry from now on.",
            ["Current PIN", "New PIN", "New PIN again"], "Change", async pins =>
            {
                if (NewPinProblem(pins[1], pins[2]) is { } problem)
                    return problem;
                return await SaveLockAsync(() => _app.Lock.ChangePin(pins[0], pins[1]) ? null : NotThePin);
            });

    private static string? NewPinProblem(string pin, string again) =>
        !AppLock.IsPin(pin) ? $"A PIN is {AppLock.PinLength} digits."
        : pin != again ? "The two PINs aren't the same."
        : null;

    /// <summary>Change the lock off the UI thread (its hash is slow on purpose); what went wrong, or null.</summary>
    private static Task<string?> SaveLockAsync(Action change) => SaveLockAsync(() =>
    {
        change();
        return null;
    });

    private static async Task<string?> SaveLockAsync(Func<string?> change)
    {
        try
        {
            return await Task.Run(change);
        }
        catch (IOException e)
        {
            return $"The app lock wasn't changed: {e.Message}";
        }
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ferry.App.Services;

/// <summary>A switch with On or Off to its left, as the mocks and Windows 11's own Settings have it.</summary>
public sealed partial class LabeledSwitch : UserControl
{
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleSwitch _switch = new()
    {
        OnContent = "",
        OffContent = "",
        MinWidth = 0,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, -12, 0), // the gap it keeps for words it doesn't have here, or its edge is short of the buttons beside it
    };

    public LabeledSwitch()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(_label);
        row.Children.Add(_switch);
        Content = row;
        ShowLabel();
        _switch.Toggled += (_, e) =>
        {
            ShowLabel();
            Toggled?.Invoke(this, e);
        };
    }

    public event RoutedEventHandler? Toggled;

    public bool IsOn
    {
        get => _switch.IsOn;
        set => _switch.IsOn = value;
    }

    /// <summary>Says what the switch does, on or off, in place of On or Off.</summary>
    public string? Caption
    {
        get;
        set
        {
            field = value;
            ShowLabel();
        }
    }

    /// <summary>What a screen reader calls the switch.</summary>
    public string AccessibleName
    {
        get => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(_switch);
        set => Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_switch, value);
    }

    private void ShowLabel() => _label.Text = Caption ?? (_switch.IsOn ? "On" : "Off");
}

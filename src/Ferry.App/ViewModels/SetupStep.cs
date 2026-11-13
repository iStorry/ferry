using System.Globalization;
using Microsoft.UI.Xaml;

namespace Ferry.App.ViewModels;

/// <summary>A step of the first run, beside the Welcome page or above the server setup: ticked once done, marked while current.</summary>
public sealed record SetupStep(int Number, string Name, bool Done, bool Current)
{
    public string Mark => Done ? "✓" : Number.ToString(CultureInfo.CurrentCulture);

    /// <summary>The current step reads strongest, steps still to come faintest.</summary>
    public double Emphasis => Current ? 1 : Done ? 0.9 : 0.65;

    public Visibility CurrentMark => Current ? Visibility.Visible : Visibility.Collapsed;
}

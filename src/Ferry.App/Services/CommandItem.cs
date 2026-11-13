using Microsoft.UI.Xaml;

namespace Ferry.App.Services;

/// <summary>A line in the command box's list: something to do, somewhere to go, or what a pasted link is.</summary>
/// <param name="Run">what choosing it does; null for a line that only says something</param>
public sealed record CommandItem(string Glyph, string Title, string Detail = "", string Keys = "", Func<Task>? Run = null)
{
    public Visibility ShowDetail => Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>What a screen reader says for it.</summary>
    public override string ToString() => Detail.Length > 0 ? $"{Title}, {Detail}" : Title;
}

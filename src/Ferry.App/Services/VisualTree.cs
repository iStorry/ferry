using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Ferry.App.Services;

/// <summary>What a control keeps in its template, where no name reaches it.</summary>
public static class VisualTree
{
    /// <summary>The first element of this type under <paramref name="root"/>, or null.</summary>
    public static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
                return found;
            if (Descendant<T>(child) is { } deeper)
                return deeper;
        }
        return null;
    }
}

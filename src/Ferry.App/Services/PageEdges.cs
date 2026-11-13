using Microsoft.UI.Xaml;

namespace Ferry.App.Services;

/// <summary>How far a page's content stays from its edges, which come in when the window is narrow.</summary>
public static class PageEdges
{
    /// <summary>Narrower than this, a page is laid out for a narrow window.</summary>
    public const double NarrowWidth = 640;

    public static Thickness For(double width) =>
        (Thickness)Application.Current.Resources[width < NarrowWidth ? "NarrowPagePadding" : "PagePadding"];
}

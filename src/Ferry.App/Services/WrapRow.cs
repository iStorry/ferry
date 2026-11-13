using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Ferry.App.Services;

/// <summary>Its children in a row, going on to another line when there is no more room, as a row of pills in a narrow window has to.</summary>
public sealed partial class WrapRow : Panel
{
    public double Spacing { get; set; } = 8;

    protected override Size MeasureOverride(Size availableSize) => Flow(availableSize.Width, arrange: false);

    protected override Size ArrangeOverride(Size finalSize)
    {
        Flow(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Flow(double width, bool arrange)
    {
        var (x, y, line, widest) = (0.0, 0.0, 0.0, 0.0);
        foreach (var child in Children)
        {
            if (!arrange)
                child.Measure(new Size(width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > width)
                (x, y, line) = (0, y + line + Spacing, 0);
            if (arrange)
                child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width + Spacing;
            line = Math.Max(line, size.Height);
            widest = Math.Max(widest, x - Spacing);
        }
        return new Size(widest, y + line);
    }
}

using Ferry.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.ViewManagement;

namespace Ferry.App.Pages;

/// <summary>What the server page draws its figures with.</summary>
public sealed partial class ServerPage
{
    /// <summary>One stretch of the crossing: a speed over a line, in the accent while data moves along it, and the files under it.</summary>
    private sealed class Leg(TextBlock speed, TextBlock unit, Line line, Storyboard flow, TextBlock note)
    {
        private const double Size = 20;
        private const double NarrowSize = 15;
        private static readonly UISettings Motion = new(); // Windows' "animation effects" setting

        private bool _flowing;

        public void Show(string figure, string unitText, string noteText, bool moving, ElementTheme theme)
        {
            (speed.Text, unit.Text, note.Text) = (figure, unitText, noteText);
            line.Stroke = ThemeBrushes.Get(moving ? ThemeBrushes.Accent : ThemeBrushes.Tertiary, theme);
            if (moving == _flowing)
                return;
            _flowing = moving;
            // in lengths of the line's thickness: dashes of 4 with gaps of 6
            line.StrokeDashArray = moving ? new DoubleCollection { 2, 3 } : new DoubleCollection();
            if (moving && Motion.AnimationsEnabled)
                flow.Begin();
            else
                flow.Stop();
        }

        public void Stop()
        {
            _flowing = false;
            line.StrokeDashArray = new DoubleCollection();
            flow.Stop();
        }

        /// <summary>In a narrow window: a smaller figure, and nothing under the line.</summary>
        public void Fit(bool narrow)
        {
            speed.FontSize = narrow ? NarrowSize : Size;
            note.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>One figure of the server's, right now: what it is, the value, a thin bar of how full, and a line under it.</summary>
    private sealed class Gauge
    {
        private readonly TextBlock _value;
        private readonly TextBlock _detail = new()
        {
            Style = (Style)Application.Current.Resources["CaptionFaintStyle"],
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        private readonly ColumnDefinition _filled = new() { Width = new GridLength(0, GridUnitType.Star) };
        private readonly ColumnDefinition _rest = new() { Width = new GridLength(1, GridUnitType.Star) };
        private readonly Border _fill = new() { CornerRadius = new CornerRadius(1.5) };
        private string _tone = ThemeBrushes.Tertiary;

        /// <param name="resources">the page's, with the styles for the figure and its bar</param>
        public Gauge(string title, string glyph, ResourceDictionary resources)
        {
            _value = new TextBlock { Text = "…", Style = (Style)resources["GaugeFigureStyle"], Margin = new Thickness(0, 6, 0, 8) };
            var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            // the page's own text colour, a little faded: it follows the theme chosen in Settings, not Windows'
            heading.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Opacity = 0.75 });
            heading.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["CaptionMutedStyle"] });
            var track = new Grid { Style = (Style)resources["GaugeTrackStyle"] };
            track.ColumnDefinitions.Add(_filled);
            track.ColumnDefinitions.Add(_rest);
            track.Children.Add(_fill);
            var body = new StackPanel();
            body.Children.Add(heading);
            body.Children.Add(_value);
            body.Children.Add(track);
            body.Children.Add(_detail);
            Root = new Border { Style = (Style)Application.Current.Resources["CardStyle"], Padding = new Thickness(14), MinHeight = 0, Child = body };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Root, title);
        }

        public Border Root { get; }

        /// <param name="share">how full the bar is, from 0 to 1 and held there</param>
        /// <param name="tone">a <see cref="ThemeBrushes"/> key for the bar</param>
        /// <param name="tip">more than the line has room for; the line itself if there is no more</param>
        public void Set(string value, string detail, double share = 0, string tone = ThemeBrushes.Tertiary, string? tip = null)
        {
            (_value.Text, _detail.Text, _tone) = (value, detail, tone);
            ToolTipService.SetToolTip(_detail, tip ?? detail);
            var part = Math.Clamp(share, 0, 1);
            _filled.Width = new GridLength(part, GridUnitType.Star);
            _rest.Width = new GridLength(1 - part, GridUnitType.Star);
        }

        public void Paint(ElementTheme theme) => _fill.Background = ThemeBrushes.Get(_tone, theme);
    }
}

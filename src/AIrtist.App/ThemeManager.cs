using System.Windows;
using System.Windows.Media;

namespace AIrtist.App;

internal static class ThemeManager
{
    internal static bool Dark { get; private set; }
    internal static void Apply(bool dark)
    {
        Dark = dark;
        var resources = Application.Current.Resources;
        var colors = new Dictionary<string, (string Light, string Dark)>
        {
            ["Ink"] = ("#29243E", "#EEE9F7"), ["Muted"] = ("#766B88", "#B9AEC9"),
            ["Accent"] = ("#7955C4", "#C4A6FF"), ["AccentSoft"] = ("#F1EDF9", "#352C48"),
            ["Surface"] = ("#FCFFFFFF", "#252331"), ["FieldSurface"] = ("#FAF8FD", "#1E1D29"),
            ["CardBorder"] = ("#E5DFF1", "#484054"), ["Success"] = ("#447566", "#9BD5BF"),
            ["SecondaryInk"] = ("#50456B", "#E1D5F5"), ["FocusBorder"] = ("#452581", "#D2B7FF"),
            ["HoverBorder"] = ("#B9A2DE", "#AF8DDF"), ["SelectionBackground"] = ("#CBB7EB", "#65517F"),
            ["DisabledSurface"] = ("#F2EFF6", "#2C2938"), ["PageBase"] = ("#F5F3FA", "#191823"),
            ["MenuGlowPurple"] = ("#B9E4DDEF", "#663C2D55"), ["MenuGlowGreen"] = ("#80DFEDE7", "#66345045")
        };
        foreach (var item in colors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? item.Value.Dark : item.Value.Light));
            brush.Freeze(); resources[item.Key] = brush;
        }
        resources["PageBackground"] = Gradient(dark ? "#201D2D" : "#F0ECFA", dark ? "#191923" : "#FAF8F5", dark ? "#1C2928" : "#EBF3F0");
        resources["PrimaryBackground"] = Gradient(dark ? "#8060B8" : "#9574D6", dark ? "#7753B6" : "#8662CA", dark ? "#6843A4" : "#7650BC");
    }
    private static Brush Gradient(string a, string b, string c)
    {
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
        brush.GradientStops.Add(new((Color)ColorConverter.ConvertFromString(a), 0));
        brush.GradientStops.Add(new((Color)ColorConverter.ConvertFromString(b), .55));
        brush.GradientStops.Add(new((Color)ColorConverter.ConvertFromString(c), 1));
        brush.Freeze(); return brush;
    }
}

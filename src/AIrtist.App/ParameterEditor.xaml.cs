using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AIrtist.Core;

namespace AIrtist.App;

public partial class ParameterEditor : UserControl
{
    public ParameterEditor() => InitializeComponent();
    public void RequireFixedSeed()
    {
        RandomSeed.IsChecked = false; RandomSeed.Visibility = Visibility.Collapsed;
    }
    private void RandomChanged(object sender, RoutedEventArgs e)
    { if (SeedInput is not null) SeedInput.IsEnabled = RandomSeed.IsChecked != true; }
    public GenerationParameters Read()
    {
        const NumberStyles number = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign;
        if ((!float.TryParse(GuidanceInput.Text.Trim(), number, CultureInfo.CurrentCulture, out float guidance)
                && !float.TryParse(GuidanceInput.Text.Trim(), number, CultureInfo.InvariantCulture, out guidance))
            || !int.TryParse(StepsInput.Text.Trim(), out int steps))
            throw new ArgumentException("CFG는 소수, Steps는 정수로 입력해 주세요.");
        int? seed = null;
        if (RandomSeed.IsChecked != true)
        {
            if (!int.TryParse(SeedInput.Text.Trim(), out int parsed)) throw new ArgumentException("Seed는 0~2147483647의 정수로 입력해 주세요.");
            seed = parsed;
        }
        var result = new GenerationParameters(guidance, steps, seed); result.Validate(); return result;
    }
    public void Set(GenerationParameters values)
    {
        GuidanceInput.Text = values.Guidance.ToString("R", CultureInfo.InvariantCulture);
        StepsInput.Text = values.Steps.ToString(CultureInfo.InvariantCulture);
        SeedInput.Text = (values.Seed ?? 42).ToString(CultureInfo.InvariantCulture);
        RandomSeed.IsChecked = values.Seed is null;
    }
    // Preserve unfinished/invalid input when switching local preview roles.
    public string[] Capture() => [GuidanceInput.Text, StepsInput.Text, SeedInput.Text, (RandomSeed.IsChecked == true).ToString()];
    public void Restore(string[]? input)
    {
        if (input is null) { Set(new()); return; }
        GuidanceInput.Text = input[0]; StepsInput.Text = input[1]; SeedInput.Text = input[2]; RandomSeed.IsChecked = input[3] == "True";
    }
}

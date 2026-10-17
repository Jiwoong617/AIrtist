using System.IO;
using System.Text.Json;
using System.Windows;

namespace AIrtist.App;

internal sealed record GameSettings(int Width = 1120, int Height = 820, int Bgm = 70, int Sfx = 70, bool DarkMode = false)
{
    internal static readonly (int Width, int Height)[] Sizes = [(1120, 820), (1280, 900), (1600, 900), (1920, 1080)];
    internal static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIrtist", "settings.json");
    internal bool Valid => Sizes.Contains((Width, Height)) && Bgm is >= 0 and <= 100 && Sfx is >= 0 and <= 100;
    internal static GameSettings Load(string? path = null)
    {
        try { var value = JsonSerializer.Deserialize<GameSettings>(File.ReadAllText(path ?? FilePath)); return value?.Valid == true ? value : new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal void Save(string? path = null)
    {
        if (!Valid) throw new ArgumentException("설정 값을 확인해 주세요.");
        path ??= FilePath; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this)); File.Move(temporary, path, true);
    }
    internal void Apply(Window window)
    {
        var area = SystemParameters.WorkArea;
        window.WindowState = WindowState.Normal;
        window.MinWidth = Math.Min(window.MinWidth, area.Width);
        window.MinHeight = Math.Min(window.MinHeight, area.Height);
        window.Width = Math.Clamp(Width, window.MinWidth, Math.Max(window.MinWidth, area.Width));
        window.Height = Math.Clamp(Height, window.MinHeight, Math.Max(window.MinHeight, area.Height));
        window.Left = area.Left + (area.Width - window.Width) / 2;
        window.Top = area.Top + (area.Height - window.Height) / 2;
    }
}

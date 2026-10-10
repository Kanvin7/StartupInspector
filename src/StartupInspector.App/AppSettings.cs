using System.IO;
using System.Text.Json;

namespace StartupInspector.App;

/// <summary>
/// 界面偏好(目前只有主题色),存在 %LOCALAPPDATA%\StartupInspector\settings.json。
/// 读写失败都退回默认值,不影响使用。
/// </summary>
internal static class AppSettings
{
    private const string DefaultAccent = "#22D3EE";

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StartupInspector",
        "settings.json");

    public static string LoadAccent()
    {
        try
        {
            if (!File.Exists(FilePath)) return DefaultAccent;

            using var stream = File.OpenRead(FilePath);
            var settings = JsonSerializer.Deserialize<SettingsModel>(stream);
            return string.IsNullOrWhiteSpace(settings?.Accent) ? DefaultAccent : settings!.Accent!;
        }
        catch
        {
            return DefaultAccent;
        }
    }

    public static void SaveAccent(string accent)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (directory is not null) Directory.CreateDirectory(directory);

            using var stream = File.Create(FilePath);
            JsonSerializer.Serialize(stream, new SettingsModel { Accent = accent });
        }
        catch
        {
            // 存不下就算了,不影响本次使用
        }
    }

    private sealed class SettingsModel
    {
        public string? Accent { get; set; }
    }
}


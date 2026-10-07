using System.Text.Json;

namespace SchrodingersCat.App.Services;

/// <summary>
/// <see cref="AppSettings"/> in a JSON file, by default <c>SchrodingersCAT/settings.json</c> in the user's application
/// data folder. A missing, unreadable or malformed file means the defaults: forgetting the last TM is better than not
/// starting. Saving writes a temporary file and moves it over the old one, so a failed save leaves it intact.
/// </summary>
public sealed class JsonSettingsStore(string path) : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SchrodingersCAT", "settings.json");

    public string FilePath { get; } = path ?? throw new ArgumentNullException(nameof(path));

    public AppSettings Load()
    {
        try
        {
            using var stream = File.OpenRead(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(stream, Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var temp = FilePath + ".tmp";
        try
        {
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(settings, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }
}

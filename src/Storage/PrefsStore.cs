using System.IO;
using System.Text.Json;

namespace SshManager.Storage;

public sealed class Prefs
{
    public string Theme { get; set; } = "default";
    public double FontSize { get; set; } = 13.5;
}

public sealed class PrefStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SshManager");
    private static readonly string FilePath = Path.Combine(Dir, "prefs.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public Prefs Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize <Prefs>(File.ReadAllText(FilePath)) ?? new();
        }
        catch {  Current = new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOpts));
    }
}
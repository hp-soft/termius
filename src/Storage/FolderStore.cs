using System.IO;
using System.Text.Json;

namespace SshManager.Storage;

public sealed class FolderStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SshManager");
    private static readonly string FilePath = Path.Combine(Dir, "folders.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    public List<string> Items { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Items = JsonSerializer.Deserialize < List<string>> (File.ReadAllText(FilePath)) ?? new();
        }
        catch { Items = new(); }

        if (Items.Count == 0)
            Items = new() { "~", "/c/wspace" };
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Items, JsonOpts));
    }

    public void Add(string path)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0 || Items.Contains(path)) return;
        Items.Add(path);
        Save();
    }

    public void Remove(string path)
    {
        Items.RemoveAll(p => p == path);
        Save();
    }
}
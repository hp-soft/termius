using System.IO;
using System.Text.Json;

namespace SshManager.Storage;

public sealed class Snippet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Commands { get; set; } = "";
}

public sealed class SnippetStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SshManager");
    private static readonly string FilePath = Path.Combine(Dir, "snippets.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public List<Snippet> Items { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Items = JsonSerializer.Deserialize<List<Snippet>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { Items = new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Items, JsonOpts));
    }

    public void Upsert(Snippet s)
    {
        var i = Items.FindIndex(x => x.Id == s.Id);
        if (i >= 0) Items[i] = s; else Items.Add(s);
        Save();
    }

    public void Remove(string id)
    {
        Items.RemoveAll(s => s.Id == id);
        Save();
    }
}
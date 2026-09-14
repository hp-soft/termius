using System.IO;
using System.Text.Json;

namespace SshManager.Storage;

public sealed class ConnectionStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SshManager");

    private static readonly string FilePath = Path.Combine(Dir, "connections.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    public List<Connection> Items { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Items = JsonSerializer.Deserialize<List<Connection>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { Items = new();  }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Items, JsonOpts));
    }

    public Connection? Get(string id) => Items.FirstOrDefault(c => c.Id == id);

    public void Upsert( Connection c )
    {
        var i = Items.FindIndex(x => x.Id == c.Id);
        if (i >= 0) Items[i] = c; else Items.Add(c);
        Save();
    }

    public void Remove(string id)
    {
        Items.RemoveAll(c => c.Id == id);
        Save();
    }

    public static void SetPassword(Connection c, string? plain)
        => c.PasswordEnc = string.IsNullOrEmpty(plain) ? null : Dpapi.Protect(plain);

    public static string? GetPassword(Connection c)
    {
        if (string.IsNullOrEmpty(c.PasswordEnc)) return null;
        try { return Dpapi.Unprotect(c.PasswordEnc);} catch { return null; }
    }
}
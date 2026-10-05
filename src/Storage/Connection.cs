namespace SshManager.Storage;

public sealed class Connection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public string AuthMethod { get; set; } = "password";
    public string? PasswordEnc { get; set; }
    // Decrypted password helper - returns null when not present or on failure
    public string? Password
    {
        get
        {
            try
            {
                if (string.IsNullOrWhiteSpace(PasswordEnc)) return null;
                return Dpapi.Unprotect(PasswordEnc);
            }
            catch { return null; }
        }
    }
    public string? KeyPath { get; set; }
    public string Group { get; set; } = "SSH";
    public string Theme { get; set; } = "default";
}
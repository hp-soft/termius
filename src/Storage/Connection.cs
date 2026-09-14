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
    public string? KeyPath { get; set; }
    public string Group { get; set; } = "SSH";
    public string Theme { get; set; } = "default";
}
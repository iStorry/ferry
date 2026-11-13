using System.Text.Json;
using Ferry.Core.Files;

namespace Ferry.Plugins.Server;

/// <summary>
/// The login's secret in the secret store: the passphrase of the key file Ferry logs in with, when it has one. A
/// login saved before OpenSSH holds the private key itself (or a password) instead; <see cref="OpenSshLogin"/> moves
/// such a key to a file, and a password is no longer used.
/// </summary>
public sealed record ServerLogin(string? PrivateKey = null, string? Passphrase = null, string? Password = null)
{
    private const string SecretName = "server-login";

    /// <summary>The saved login, or null if none is saved.</summary>
    public static ServerLogin? Load(SecretStore secrets) =>
        secrets.Load(SecretName) is { } bytes ? JsonSerializer.Deserialize(bytes, ServerFileJson.Default.ServerLogin) : null;

    public void Save(SecretStore secrets) => secrets.Save(SecretName, JsonSerializer.SerializeToUtf8Bytes(this, ServerFileJson.Default.ServerLogin));

    public static void Forget(SecretStore secrets) => secrets.Delete(SecretName);

    /// <summary>Says what is saved, never what the secret is.</summary>
    public override string ToString() =>
        PrivateKey is not null ? $"key{(Passphrase is null ? "" : " with passphrase")}"
        : Password is not null ? "password"
        : Passphrase is not null ? "passphrase"
        : "none";
}

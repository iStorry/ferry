using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ferry.Core.Json;
using Ferry.Core.Files;

namespace Ferry.Core.AppLocking;

public enum LockStatus
{
    Off,
    On,

    /// <summary>A lock is saved but can't be decrypted or read: the app stays locked rather than open.</summary>
    Unreadable,
}

public enum PinCheck
{
    Unlocked,
    Wrong,

    /// <summary>Too many wrong PINs in a row: open with nothing in it, as a new install would.</summary>
    Decoy,
}

/// <summary>The app lock as the secret store keeps it: never the PIN, only a salted PBKDF2 hash of it.</summary>
/// <param name="WrongTries">wrong PINs since the last right one (or the last decoy), kept across restarts</param>
public sealed record LockSecret(byte[] Salt, byte[] Hash, int Iterations, int DecoyAfter, int WrongTries);

/// <summary>
/// The Windows app's 4-digit PIN lock. It is kept in the secret store, not in settings.json, where an environment
/// variable could turn it off. One call at a time: each check derives a key on purpose slowly, so run it off the UI
/// thread.
/// </summary>
public sealed class AppLock
{
    public const int PinLength = 4;

    /// <summary>Wrong PINs never open the decoy.</summary>
    public const int DecoyOff = 0;

    public const int DefaultDecoyAfter = 5;

    private const string SecretName = "app-lock";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    // OWASP's figure for PBKDF2-SHA256
    private const int DefaultIterations = 600_000;

    private readonly SecretStore _secrets;
    private readonly int _iterations;
    private LockSecret? _secret;

    public AppLock(SecretStore secrets)
        : this(secrets, DefaultIterations)
    {
    }

    internal AppLock(SecretStore secrets, int iterations)
    {
        _secrets = secrets;
        _iterations = iterations;
        _secret = Read(secrets);
        Status = _secret is not null ? LockStatus.On
            : secrets.Has(SecretName) ? LockStatus.Unreadable
            : LockStatus.Off;
    }

    /// <summary>How many wrong PINs in a row open the decoy: <see cref="DecoyOff"/> or one of these.</summary>
    public static IReadOnlyList<int> DecoyChoices { get; } = [DecoyOff, 3, 5, 10];

    public LockStatus Status { get; private set; }

    public int DecoyAfter => _secret?.DecoyAfter ?? DefaultDecoyAfter;

    /// <summary>Where the lock is saved: removing the file is how a forgotten PIN is got past.</summary>
    public string FilePath => _secrets.PathOf(SecretName);

    public static bool IsPin(string text) => text.Length == PinLength && text.All(char.IsAsciiDigit);

    /// <summary>Lock the app with this PIN, replacing any lock saved before.</summary>
    /// <exception cref="ArgumentException">not a 4-digit PIN, or not one of the <see cref="DecoyChoices"/></exception>
    /// <exception cref="IOException">the lock can't be saved; it is as it was</exception>
    public void TurnOn(string pin, int decoyAfter)
    {
        if (!DecoyChoices.Contains(decoyAfter))
            throw new ArgumentException($"{decoyAfter} is not one of the choices.", nameof(decoyAfter));
        Save(NewSecret(pin, decoyAfter));
    }

    /// <summary>
    /// The try is counted before the PIN is checked, so ending the app in the middle of a check doesn't give a free
    /// one. The count goes back to 0 on the right PIN, and when the decoy opens.
    /// </summary>
    /// <exception cref="InvalidOperationException">the lock is not <see cref="LockStatus.On"/></exception>
    /// <exception cref="IOException">the try can't be counted, so the PIN was not checked</exception>
    public PinCheck Check(string pin)
    {
        var secret = Require();
        var counted = secret with { WrongTries = secret.WrongTries + 1 };
        Save(counted);
        if (Matches(secret, pin))
        {
            Save(secret with { WrongTries = 0 });
            return PinCheck.Unlocked;
        }
        if (secret.DecoyAfter == DecoyOff || counted.WrongTries < secret.DecoyAfter)
            return PinCheck.Wrong;
        Save(secret with { WrongTries = 0 });
        return PinCheck.Decoy;
    }

    /// <summary>False, and nothing changed, if <paramref name="current"/> is not the PIN.</summary>
    /// <exception cref="ArgumentException"><paramref name="next"/> is not a 4-digit PIN</exception>
    /// <exception cref="InvalidOperationException">the lock is not <see cref="LockStatus.On"/></exception>
    /// <exception cref="IOException">the new PIN can't be saved; the old one still opens the app</exception>
    public bool ChangePin(string current, string next)
    {
        var secret = Require();
        if (!Matches(secret, current))
            return false;
        Save(NewSecret(next, secret.DecoyAfter));
        return true;
    }

    /// <exception cref="ArgumentException">not one of the <see cref="DecoyChoices"/></exception>
    /// <exception cref="InvalidOperationException">the lock is not <see cref="LockStatus.On"/></exception>
    /// <exception cref="IOException">the choice can't be saved; it is as it was</exception>
    public void SetDecoyAfter(int decoyAfter)
    {
        if (!DecoyChoices.Contains(decoyAfter))
            throw new ArgumentException($"{decoyAfter} is not one of the choices.", nameof(decoyAfter));
        Save(Require() with { DecoyAfter = decoyAfter });
    }

    /// <summary>False, and the app stays locked, if <paramref name="pin"/> is not the PIN.</summary>
    /// <exception cref="InvalidOperationException">the lock is not <see cref="LockStatus.On"/></exception>
    /// <exception cref="IOException">the lock can't be removed</exception>
    public bool TurnOff(string pin)
    {
        if (!Matches(Require(), pin))
            return false;
        _secrets.Delete(SecretName);
        (_secret, Status) = (null, LockStatus.Off);
        return true;
    }

    private LockSecret Require() =>
        Status == LockStatus.On && _secret is { } secret ? secret : throw new InvalidOperationException($"The app lock is {Status}.");

    private LockSecret NewSecret(string pin, int decoyAfter)
    {
        if (!IsPin(pin))
            throw new ArgumentException($"A PIN is {PinLength} digits.", nameof(pin));
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return new LockSecret(salt, Hash(pin, salt, _iterations), _iterations, decoyAfter, WrongTries: 0);
    }

    private static bool Matches(LockSecret secret, string pin) =>
        IsPin(pin) && CryptographicOperations.FixedTimeEquals(Hash(pin, secret.Salt, secret.Iterations), secret.Hash);

    private static byte[] Hash(string pin, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, HashBytes);

    private void Save(LockSecret secret)
    {
        _secrets.Save(SecretName, JsonSerializer.SerializeToUtf8Bytes(secret, FerryFileJson.Default.LockSecret));
        (_secret, Status) = (secret, LockStatus.On);
    }

    private static LockSecret? Read(SecretStore secrets)
    {
        if (secrets.Load(SecretName) is not { } bytes)
            return null;
        try
        {
            return JsonSerializer.Deserialize(bytes, FerryFileJson.Default.LockSecret) is { Salt.Length: SaltBytes, Hash.Length: HashBytes, Iterations: > 0, WrongTries: >= 0 } secret
                && DecoyChoices.Contains(secret.DecoyAfter)
                ? secret
                : null;
        }
        catch (JsonException)
        {
            return null; // the file is there: Unreadable, and the app stays locked
        }
    }
}

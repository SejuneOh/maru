using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace WorkBoard.Api.Infrastructure.Credentials;

public sealed class CredentialKeyException(string message, Exception? inner = null) : Exception(message, inner);

// Version -> 32-byte key. Rows are sealed with CurrentVersion; any listed version can still open its rows.
public sealed class CredentialKeyring
{
    public const int KeySize = 32;

    private readonly IReadOnlyDictionary<int, byte[]> _keys;

    public CredentialKeyring(int currentVersion, IReadOnlyDictionary<int, byte[]> keys)
    {
        if (!keys.ContainsKey(currentVersion))
            throw new CredentialKeyException("The current key version is not in the key file.");
        if (keys.Any(k => k.Key <= 0 || k.Value.Length != KeySize))
            throw new CredentialKeyException($"Every key must have a positive version and be {KeySize} bytes.");
        CurrentVersion = currentVersion;
        _keys = keys;
    }

    public int CurrentVersion { get; }

    public byte[] Get(int version) =>
        _keys.TryGetValue(version, out var key) ? key : throw new CredentialKeyException($"Key version {version} is not in the key file.");

    // {"current": 2, "keys": {"1": "<base64>", "2": "<base64>"}}
    public static CredentialKeyring Parse(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<KeyFile>(json, JsonSerializerOptions.Web)
                       ?? throw new CredentialKeyException("The key file is empty.");
            var keys = (file.Keys ?? []).ToDictionary(k => int.Parse(k.Key), k => Convert.FromBase64String(k.Value));
            return new CredentialKeyring(file.Current, keys);
        }
        catch (Exception e) when (e is JsonException or FormatException or OverflowException)
        {
            throw new CredentialKeyException("The key file is not valid.", e);
        }
    }

    public static CredentialKeyring Load(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                const UnixFileMode GroupOrOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                if ((File.GetUnixFileMode(path) & GroupOrOther) != 0)
                    throw new CredentialKeyException("The key file must not be accessible by group or others (chmod 600).");
            }
            return Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CredentialKeyException("GitHub:Credentials:KeyFile does not point to a readable key file.", e);
        }
    }

    public static string NewKeyFileJson(int version = 1) =>
        JsonSerializer.Serialize(new KeyFile(version, new Dictionary<string, string>
        {
            [version.ToString()] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeySize)),
        }), JsonSerializerOptions.Web);

    private sealed record KeyFile(int Current, Dictionary<string, string>? Keys);
}

public interface ICredentialKeys
{
    CredentialKeyring Get();
}

// Read once per process, like the App private key: a wrong path fails every call the same way until the API restarts.
public sealed class FileCredentialKeys(IOptions<CredentialOptions> options, IHostEnvironment env) : ICredentialKeys
{
    private readonly Lazy<CredentialKeyring> _ring = new(() =>
    {
        var path = options.Value.KeyFile;
        if (!string.IsNullOrWhiteSpace(path))
            return CredentialKeyring.Load(Path.GetFullPath(path, env.ContentRootPath));
        if (!string.IsNullOrWhiteSpace(options.Value.Key))
            return CredentialKeyring.Parse(options.Value.Key);
        throw new CredentialKeyException("Neither GitHub:Credentials:KeyFile nor GitHub:Credentials:Key is set.");
    });

    public CredentialKeyring Get() => _ring.Value;
}

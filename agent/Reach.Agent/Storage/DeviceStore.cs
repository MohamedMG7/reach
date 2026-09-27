using System.Buffers.Text;
using System.Text.Json;

namespace Reach.Agent.Storage;

/// <param name="PublicKey">The phone's static Noise public key, base64url.</param>
public sealed record Device(string Id, string Name, string PublicKey, DateTimeOffset PairedAt, DateTimeOffset LastSeenAt);

/// <summary>Paired phones, persisted to devices.json. Thread-safe.</summary>
public sealed class DeviceStore
{
    public const string DefaultName = "New phone";
    public const int MaxNameLength = 64;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private List<Device> _devices;

    public DeviceStore(string path, TimeProvider time)
    {
        _path = path;
        _time = time;
        _devices = Load(path);
    }

    /// <summary>Raised after any change, outside the lock.</summary>
    public event Action? Changed;

    public IReadOnlyList<Device> All
    {
        get { lock (_lock) return [.. _devices]; }
    }

    public Device? FindByKey(byte[] publicKey)
    {
        var key = Base64Url.EncodeToString(publicKey);
        lock (_lock) return _devices.FirstOrDefault(d => d.PublicKey == key);
    }

    public Device Add(byte[] publicKey)
    {
        var now = _time.GetUtcNow();
        var device = new Device(Guid.NewGuid().ToString("N"), DefaultName, Base64Url.EncodeToString(publicKey), now, now);
        Mutate(list => list.Add(device));
        return device;
    }

    /// <summary>Sets the display name (from the phone's hello) and the last-seen time.</summary>
    public void Seen(string id, string name)
    {
        name = name.Trim();
        if (name.Length == 0) name = DefaultName;
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        var now = _time.GetUtcNow();
        Mutate(list =>
        {
            var i = list.FindIndex(d => d.Id == id);
            if (i >= 0) list[i] = list[i] with { Name = name, LastSeenAt = now };
        });
    }

    public bool Remove(string id)
    {
        var removed = false;
        Mutate(list => removed = list.RemoveAll(d => d.Id == id) > 0);
        return removed;
    }

    private void Mutate(Action<List<Device>> change)
    {
        lock (_lock)
        {
            var copy = new List<Device>(_devices);
            change(copy);
            Save(copy);
            _devices = copy;
        }
        Changed?.Invoke();
    }

    private void Save(List<Device> devices)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(devices, Json));
        File.Move(temp, _path, overwrite: true);
    }

    /// <summary>
    /// Antivirus and sync tools (OneDrive) briefly hold files open; wait them out rather than fail
    /// to start. A file that stays locked still throws.
    /// </summary>
    private static List<Device> Load(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return LoadOnce(path);
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>
    /// A file that can't be parsed is moved aside to devices.json.corrupt and the store starts empty:
    /// failing closed (phones must re-pair) beats refusing to start or trusting a half-read list.
    /// </summary>
    private static List<Device> LoadOnce(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var devices = JsonSerializer.Deserialize<List<Device>>(File.ReadAllBytes(path), Json);
            if (devices is not null && devices.All(IsValid)) return devices;
        }
        catch (JsonException)
        {
        }
        File.Move(path, path + ".corrupt", overwrite: true);
        return [];
    }

    private static bool IsValid(Device? d) =>
        d is { Id: { Length: > 0 }, Name: not null, PublicKey: { Length: 43 } };
}

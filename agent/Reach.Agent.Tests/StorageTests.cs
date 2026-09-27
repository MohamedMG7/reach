using System.Security.Cryptography;
using Microsoft.Extensions.Time.Testing;
using Reach.Agent.Storage;
using Reach.Protocol;

namespace Reach.Agent.Tests;

public class IdentityStoreTests
{
    [Fact]
    public void CreatesAKeyOnceAndReloadsTheSameKey()
    {
        using var dir = new TempDir();
        var first = IdentityStore.LoadOrCreate(dir.File("identity.key"));
        var second = IdentityStore.LoadOrCreate(dir.File("identity.key"));
        Assert.Equal(first.PublicKey, second.PublicKey);
        Assert.Equal(first.SecretKey, second.SecretKey);
    }

    [Fact]
    public void NeverWritesTheSecretKeyInTheClear()
    {
        using var dir = new TempDir();
        var pair = IdentityStore.LoadOrCreate(dir.File("identity.key"));
        var onDisk = File.ReadAllBytes(dir.File("identity.key"));
        Assert.Equal(-1, onDisk.AsSpan().IndexOf(pair.SecretKey));
    }

    public static TheoryData<byte[]> UnusableKeyFiles => new()
    {
        new byte[] { 1, 2, 3, 4, 5 }, // not DPAPI data, or from another Windows user / install
        ProtectedData.Protect(new byte[16], null, DataProtectionScope.CurrentUser), // decrypts, wrong length
    };

    [Theory]
    [MemberData(nameof(UnusableKeyFiles))]
    public void AnUnusableKeyIsMovedAsideAndReplaced(byte[] content)
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("identity.key"), content);

        var pair = IdentityStore.LoadOrCreate(dir.File("identity.key"), out var replaced);

        Assert.True(replaced);
        Assert.Equal(content, File.ReadAllBytes(dir.File("identity.key.corrupt")));
        Assert.Equal(pair.PublicKey, IdentityStore.LoadOrCreate(dir.File("identity.key"), out var again).PublicKey);
        Assert.False(again);
    }
}

public class DeviceStoreTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 26, 20, 0, 0, TimeSpan.Zero));

    [Fact]
    public void AddsAndFindsADeviceByItsPublicKey()
    {
        using var dir = new TempDir();
        var store = new DeviceStore(dir.File("devices.json"), _time);
        var key = KeyPair.Generate().PublicKey;

        var device = store.Add(key);

        Assert.Equal(device, store.FindByKey(key));
        Assert.Null(store.FindByKey(KeyPair.Generate().PublicKey));
        Assert.Equal(DeviceStore.DefaultName, device.Name);
    }

    [Fact]
    public void PersistsAcrossInstances()
    {
        using var dir = new TempDir();
        var key = KeyPair.Generate().PublicKey;
        var device = new DeviceStore(dir.File("devices.json"), _time).Add(key);

        var reloaded = new DeviceStore(dir.File("devices.json"), _time);

        Assert.Equal(device, reloaded.FindByKey(key));
    }

    [Fact]
    public void SeenUpdatesTheNameAndLastSeenTime()
    {
        using var dir = new TempDir();
        var store = new DeviceStore(dir.File("devices.json"), _time);
        var device = store.Add(KeyPair.Generate().PublicKey);
        _time.Advance(TimeSpan.FromHours(1));

        store.Seen(device.Id, "  Bedroom iPhone  ");

        var updated = store.All.Single();
        Assert.Equal("Bedroom iPhone", updated.Name);
        Assert.Equal(_time.GetUtcNow(), updated.LastSeenAt);
        Assert.Equal(device.PairedAt, updated.PairedAt);
    }

    [Fact]
    public void SeenCapsOverlongNames()
    {
        using var dir = new TempDir();
        var store = new DeviceStore(dir.File("devices.json"), _time);
        var device = store.Add(KeyPair.Generate().PublicKey);

        store.Seen(device.Id, new string('x', 500));

        Assert.Equal(DeviceStore.MaxNameLength, store.All.Single().Name.Length);
    }

    [Fact]
    public void RemoveForgetsTheDeviceAndRaisesChanged()
    {
        using var dir = new TempDir();
        var store = new DeviceStore(dir.File("devices.json"), _time);
        var key = KeyPair.Generate().PublicKey;
        var device = store.Add(key);
        var changes = 0;
        store.Changed += () => changes++;

        Assert.True(store.Remove(device.Id));

        Assert.Null(store.FindByKey(key));
        Assert.Null(new DeviceStore(dir.File("devices.json"), _time).FindByKey(key));
        Assert.Equal(1, changes);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"id\":\"x\"}")]
    [InlineData("[null]")]
    [InlineData("[{\"id\":\"x\",\"name\":\"a\",\"publicKey\":\"short\",\"pairedAt\":\"2026-09-26T00:00:00Z\",\"lastSeenAt\":\"2026-09-26T00:00:00Z\"}]")]
    public void ACorruptFileIsMovedAsideAndTheStoreStartsEmpty(string content)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("devices.json"), content);

        var store = new DeviceStore(dir.File("devices.json"), _time);

        Assert.Empty(store.All);
        Assert.Equal(content, File.ReadAllText(dir.File("devices.json.corrupt")));
        Assert.False(File.Exists(dir.File("devices.json")));
    }

    [Fact]
    public async Task AFileBrieflyLockedByAnotherProgramIsStillLoaded()
    {
        using var dir = new TempDir();
        var key = KeyPair.Generate().PublicKey;
        new DeviceStore(dir.File("devices.json"), _time).Add(key);
        var locked = new FileStream(dir.File("devices.json"), FileMode.Open, FileAccess.Read, FileShare.None); // e.g. antivirus, OneDrive
        var release = Task.Delay(300).ContinueWith(_ => locked.Dispose());

        var store = new DeviceStore(dir.File("devices.json"), _time);

        await release;
        Assert.NotNull(store.FindByKey(key));
    }
}

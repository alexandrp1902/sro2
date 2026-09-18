using Microsoft.Extensions.Logging.Abstractions;
using Sro.Server.Accounts;

namespace Sro.Server.Tests;

/// <summary>Аккаунты на диске (GDD §61–62): вход, ключи устройств, сохранение и чтение после перезапуска.</summary>
public sealed class AccountStoreTests : IDisposable
{
    /// <summary>Мало итераций: тесту незачем ждать настоящий PBKDF2.</summary>
    private const int Iterations = 1000;

    private static readonly AccountProfile Profile = new(
        1234, "heavy", "laser", ["heavy", "light"], ["laser", "pulse"], new Dictionary<string, int> { ["metal"] = 3 });

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sro-accounts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private AccountStore Open() => new(_dir, NullLogger.Instance, Iterations, autoFlush: false);

    [Fact]
    public void FreeName_CreatesAccount_AndGivesTheDeviceAKey()
    {
        using var store = Open();

        var login = store.Login("Alice", "secret");

        Assert.True(login.Ok);
        Assert.True(login.Created);
        Assert.Equal("Alice", login.Name);
        Assert.NotNull(login.Key);
        Assert.Null(store.Profile(login.Id)); // в систему пилот ещё не заходил
    }

    [Fact]
    public void TakenName_IsALogin_InAnyCase()
    {
        using var store = Open();
        var first = store.Login("Alice", "secret");

        var again = store.Login("  aLiCe ", "secret");

        Assert.True(again.Ok);
        Assert.False(again.Created);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal("Alice", again.Name); // ник — как его записали при заведении
        Assert.NotEqual(first.Key, again.Key); // у каждого устройства свой ключ
    }

    [Fact]
    public void WrongPassword_IsRefused()
    {
        using var store = Open();
        store.Login("Alice", "secret");

        Assert.Equal(LoginError.WrongPassword, store.Login("Alice", "Secret").Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("Al")]
    public void ShortName_IsRefused(string? name)
    {
        using var store = Open();
        Assert.Equal(LoginError.BadName, store.Login(name, "secret").Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("abc")]
    public void ShortPassword_IsRefused(string? password)
    {
        using var store = Open();
        Assert.Equal(LoginError.BadPassword, store.Login("Alice", password).Error);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Key_LogsInWithoutPassword()
    {
        using var store = Open();
        var login = store.Login("Alice", "secret");

        var resumed = store.Resume(login.Key);

        Assert.True(resumed.Ok);
        Assert.Equal((login.Id, "Alice"), (resumed.Id, resumed.Name));
        Assert.Null(resumed.Key); // новый ключ — только за пароль
        Assert.Equal(LoginError.BadKey, store.Resume("not-a-key").Error);
        Assert.Equal(LoginError.BadKey, store.Resume(null).Error);
    }

    [Fact]
    public void OldestKey_IsForgotten_AfterTooManyDevices()
    {
        using var store = Open();
        var keys = Enumerable.Range(0, AccountStore.MaxKeys + 1).Select(_ => store.Login("Alice", "secret").Key).ToList();

        Assert.Equal(LoginError.BadKey, store.Resume(keys[0]).Error);
        Assert.All(keys.Skip(1), key => Assert.True(store.Resume(key).Ok));
    }

    [Fact]
    public void Profile_AndLogin_SurviveRestart()
    {
        string id, key;
        using (var store = Open())
        {
            var login = store.Login("Alice", "secret");
            (id, key) = (login.Id, login.Key!);
            store.Save(id, Profile);
            store.Flush();
        }

        using var reopened = Open();

        var profile = reopened.Profile(id)!;
        Assert.Equal((1234, "heavy", "laser"), (profile.Credits, profile.Hull, profile.Weapon));
        Assert.Equal(["heavy", "light"], profile.Hulls);
        Assert.Equal(3, profile.Cargo["metal"]);
        Assert.True(reopened.Resume(key).Ok);
        Assert.Equal(id, reopened.Login("alice", "secret").Id);
        Assert.Equal(LoginError.WrongPassword, reopened.Login("alice", "wrong").Error);
    }

    [Fact]
    public void Disk_IsWrittenOnFlush_ThroughATemporaryFile()
    {
        using var store = Open();
        var id = store.Login("Alice", "secret").Id;
        var path = Path.Combine(_dir, id + ".json");
        Assert.False(File.Exists(path));

        store.Flush();

        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void File_KeepsNeitherPasswordNorKey()
    {
        using var store = Open();
        var login = store.Login("Alice", "hunter2secret");
        store.Flush();

        var text = File.ReadAllText(Path.Combine(_dir, login.Id + ".json"));

        Assert.DoesNotContain("hunter2secret", text);
        Assert.DoesNotContain(login.Key!, text);
    }

    [Fact]
    public void File_IsReadableByHand()
    {
        using var store = Open();
        var login = store.Login("Рейнджер", "secret");
        store.Flush();

        var text = File.ReadAllText(Path.Combine(_dir, login.Id + ".json"));

        Assert.Contains("\"name\": \"Рейнджер\"", text); // кириллица как есть, с отступами
    }

    [Fact]
    public void BrokenFile_IsSkipped_AndLeftForRepair()
    {
        using (var store = Open())
        {
            store.Login("Alice", "secret");
            store.Flush();
        }
        var broken = Path.Combine(_dir, "broken.json");
        File.WriteAllText(broken, "{ not json");

        using var reopened = Open();

        Assert.Equal(1, reopened.Count);
        Assert.True(File.Exists(broken));
    }
}

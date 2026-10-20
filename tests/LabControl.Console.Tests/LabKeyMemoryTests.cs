using LabControl.Console.Services;
using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Protection;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// Remembering the lab key on this computer (D-82): a device holder whose secret is only in the
/// OS store opens the key with no passphrase here, the passphrase and the recovery code still
/// work, the key document carried elsewhere still needs them, and forgetting takes it all back.
/// The file protector stands in for the keychain so the tests never touch the developer's.
/// </summary>
public class LabKeyMemoryTests
{
    private const string Instance = "6f1d0b8a-3a3c-4a8f-9b55-2b0f4c6b1d77";

    private static (LabStore Store, LabKeyVault Vault, RecoveryCode Recovery) NewLab()
    {
        var store = new LabStore(TestConsole.TempDirectory());
        store.EnsureDirectories();
        var key = LabKey.Create("Room 214", TestConsole.HolderName, TestConsole.Passphrase, out var recovery, iterations: TestConsole.Iterations);
        store.SaveLabKey(key.Document);
        var vault = new LabKeyVault(store, key.Document) { Memory = Memory(store) };
        vault.Adopt(key);
        return (store, vault, recovery);
    }

    private static LabKeyMemory Memory(LabStore store) => new(store, Instance, () => new FileSecretProtector());

    /// <summary>The console starting again: the saved document, a fresh vault, the same memory.</summary>
    private static LabKeyVault Reopened(LabStore store) => new(store, store.LoadLabKey()) { Memory = Memory(store) };

    [Fact]
    public void A_remembered_key_opens_itself_after_a_restart_without_a_passphrase()
    {
        var (store, vault, _) = NewLab();
        Assert.True(vault.Remember());
        Assert.True(vault.IsRemembered);
        Assert.True(File.Exists(store.DeviceKeyPath));
        Assert.Contains(LabKeyMemory.HolderNameOf(Instance), store.LoadLabKey().Wrappings.Select(w => w.Name));
        vault.Dispose();

        var restarted = Reopened(store);
        Assert.False(restarted.IsUnlocked);
        Assert.True(restarted.IsRemembered);

        // Any CA operation reopens it: no prompt, no passphrase.
        Assert.True(restarted.Use(key => key.LabId, out var labId));
        Assert.Equal(store.LoadLabKey().LabId, labId);
        Assert.True(restarted.IsUnlocked);

        // The idle lock is still there; the next use simply reopens it.
        restarted.Lock();
        Assert.NotNull(restarted.Peek());
    }

    [Fact]
    public void The_passphrase_and_the_recovery_code_keep_working_and_a_second_remember_replaces_the_first()
    {
        var (store, vault, recovery) = NewLab();
        Assert.True(vault.Remember());
        Assert.True(vault.Remember());
        Assert.Single(store.LoadLabKey().Wrappings, w => w.Name == LabKeyMemory.HolderNameOf(Instance));

        var byPassphrase = new LabKeyVault(store, store.LoadLabKey());
        Assert.True(byPassphrase.TryUnlock(TestConsole.Passphrase));
        var byCode = new LabKeyVault(store, store.LoadLabKey());
        Assert.True(byCode.TryUnlock(recovery));
    }

    [Fact]
    public void The_key_carried_to_another_computer_still_needs_the_passphrase()
    {
        var (store, vault, _) = NewLab();
        Assert.True(vault.Remember());

        // A backup or a copied directory carries the key document and its device holder, never device-key.json or the secret.
        var elsewhere = new LabStore(TestConsole.TempDirectory());
        elsewhere.EnsureDirectories();
        elsewhere.SaveLabKey(store.LoadLabKey());
        var other = new LabKeyVault(elsewhere, elsewhere.LoadLabKey()) { Memory = new LabKeyMemory(elsewhere, "another-instance", () => new FileSecretProtector()) };

        Assert.False(other.IsRemembered);
        Assert.False(other.TryReopen());
        Assert.False(other.Use(_ => { }));
        Assert.True(other.TryUnlock(TestConsole.Passphrase));
    }

    [Fact]
    public void Forgetting_removes_the_holder_the_file_and_the_secret_and_the_passphrase_is_needed_again()
    {
        var (store, vault, _) = NewLab();
        Assert.True(vault.Remember());
        vault.Lock();

        vault.Forget();

        Assert.False(vault.IsRemembered);
        Assert.False(File.Exists(store.DeviceKeyPath));
        Assert.DoesNotContain(LabKeyMemory.HolderNameOf(Instance), store.LoadLabKey().Wrappings.Select(w => w.Name));
        var restarted = Reopened(store);
        Assert.False(restarted.TryReopen());
        Assert.True(restarted.TryUnlock(TestConsole.Passphrase, remember: true));
        Assert.True(Reopened(store).TryReopen());
    }

    [Fact]
    public void A_torn_or_foreign_device_file_means_not_remembered_never_an_exception()
    {
        var (store, vault, _) = NewLab();
        Assert.True(vault.Remember());

        File.WriteAllText(store.DeviceKeyPath, "{ not json");
        var torn = Reopened(store);
        Assert.False(torn.IsRemembered);
        Assert.False(torn.TryReopen());

        Assert.True(vault.Remember());
        var other = new LabKeyVault(store, store.LoadLabKey()) { Memory = new LabKeyMemory(store, "someone-else", () => new FileSecretProtector()) };
        Assert.False(other.IsRemembered);
        Assert.False(other.TryReopen());
        Assert.True(other.TryUnlock(TestConsole.Passphrase));
    }

    [Fact]
    public void Remembering_needs_the_key_open_or_remembered()
    {
        var store = new LabStore(TestConsole.TempDirectory());
        store.EnsureDirectories();
        var key = LabKey.Create("Room 214", TestConsole.HolderName, TestConsole.Passphrase, out _, iterations: TestConsole.Iterations);
        store.SaveLabKey(key.Document);
        key.Dispose();
        var locked = new LabKeyVault(store, store.LoadLabKey()) { Memory = Memory(store) };

        Assert.False(locked.Remember());
        Assert.False(File.Exists(store.DeviceKeyPath));
        Assert.True(LabKeyMemory.IsDeviceHolder(LabKeyMemory.HolderNameOf(Instance)));
        Assert.False(LabKeyMemory.IsDeviceHolder(TestConsole.HolderName));
        Assert.StartsWith(Defaults.DeviceHolderPrefix, LabKeyMemory.HolderNameOf(Instance), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_remembers_its_key_and_reopens_it_for_a_ca_operation_after_the_idle_lock()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var vault = console.Session.Vault!;
        Assert.NotNull(vault.Memory);
        Assert.True(vault.Remember());

        vault.Lock();
        Assert.False(vault.IsUnlocked);

        // A PC enrolling needs the key to sign its certificate: it reopens with nobody typing anything.
        await using var agent = TestAgent.Install(console, 3, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(agent.AgentId), TimeSpan.FromSeconds(15)));
        Assert.True(vault.IsUnlocked);
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using LabControl.Console.Services;
using LabControl.Shared;
using LabControl.Shared.Files;
using LabControl.Shared.Identity;
using LabControl.Shared.Link;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;
using LabControl.Shared.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

return await Drill.Run(args);

internal static class Drill
{
    public static async Task<int> Run(string[] args)
    {
        if (!OperatingSystem.IsMacOS()) return 2;
        var count = 14;
        var mib = 500;
        for (var i = 0; i < args.Length; i++)
        {
            if (++i >= args.Length || !int.TryParse(args[i], out var value)) return 2;
            switch (args[i - 1]) { case "--count": count = value; break; case "--mib": mib = value; break; default: return 2; }
        }
        if (count is < 1 or > Defaults.MaxStudentPcs || mib is < 1 or > 2048) return 2;
        var root = Directory.CreateTempSubdirectory("labcontrol-transfer-drill-").FullName;
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var links = new List<AgentLink>();
        var stores = new List<DirectoryAgentStore>();
        var keyBytes = RandomNumberGenerator.GetBytes(32);
        var sizes = new long[count];
        try
        {
            var size = mib * 1024L * 1024;
            var source = Path.Combine(root, "source.bin");
            var block = RandomNumberGenerator.GetBytes(1024 * 1024);
            await using (var file = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1024 * 1024, true))
                for (var written = 0L; written < size; written += block.Length) await file.WriteAsync(block);
            var lab = LabKey.Create("Disposable transfer drill", "Fixture", Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)), out _);
            var store = new LabStore(Path.Combine(root, "console"));
            store.EnsureDirectories(); store.SaveLabKey(lab.Document);
            var instance = ConsoleInstance.Mint(lab, "Disposable transfer drill", new FileSecretProtector());
            store.SaveInstance(instance.Document);
            var vault = new LabKeyVault(store, lab.Document); vault.Adopt(lab);
            var beaconPort = 0;
            do
            {
                using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                beaconPort = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
            } while (beaconPort == Defaults.BeaconPort);
            await using var session = new LabSession(new ConsoleOptions { DataDirectory = store.Directory,
                BindAddress = IPAddress.Loopback, Port = 0, BeaconPort = beaconPort },
                store, vault, instance, instance.Document, NullLoggerFactory.Instance);
            await session.StartAsync();
            if (session.Port == Defaults.ConsolePort) throw new IOException("Ephemeral port matched production.");
            var codes = session.Enrollment.Generate(count, "Disposable transfer drill", DateTimeOffset.UtcNow);
            var protection = new KeyProtection(bytes => Protect(keyBytes, bytes), bytes => Unprotect(keyBytes, bytes));
            var linked = new int[count];
            for (var i = 0; i < count; i++)
            {
                var number = i + 1;
                var config = new AgentConfigDocument { LabId = session.LabId, AgentId = Guid.NewGuid().ToString("D"),
                    Number = number, Hostname = string.Format(Defaults.MachineNameFormat, number),
                    Mac = $"02:00:5E:00:00:{number:X2}", EnrollmentCode = codes[i],
                    ConsoleHost = IPAddress.Loopback.ToString(), ConsolePort = session.Port };
                var agentStore = DirectoryAgentStore.Install(Path.Combine(root, "agent-" + number), config, session.Authority, protection);
                stores.Add(agentStore);
                var link = new AgentLink(agentStore, new Behaviour(), NullLogger.Instance);
                var index = i;
                link.Linked += (_, _) => Interlocked.Increment(ref linked[index]);
                links.Add(link); link.Start();
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            await WaitUntil(() => links.All(link => link.IsLinked), TimeSpan.FromSeconds(30), deadline.Token);
            var offer = session.Files.OfferFile(source);
            long breakOffset = 0;
            var watch = Stopwatch.StartNew();
            var transfers = links.Select(async (link, index) =>
            {
                var path = Path.Combine(root, "sink-" + index + ".bin");
                await using (var destination = new Sink(path, async total =>
                {
                    Interlocked.Exchange(ref sizes[index], total);
                    if (index == 0 && total >= size / 3 && Interlocked.CompareExchange(ref breakOffset, total, 0) == 0)
                    {
                        link.Disconnect("Deliberate isolated transfer drill link break");
                        await WaitUntil(() => !link.IsLinked, TimeSpan.FromSeconds(10), deadline.Token);
                    }
                }))
                {
                    var transferred = await link.PullFileAsync(offer.Reference, offer.Sha256, destination, deadline.Token);
                    if (transferred != size || destination.Written != size) throw new IOException("Received byte count differs.");
                }
                if (new FileInfo(path).Length != size || FileHash.Sha256HexOfFile(path) != offer.Sha256)
                    throw new IOException("Independent disk SHA-256 verification failed.");
                return true;
            }).ToArray();
            var all = Task.WhenAll(transfers);
            while (!all.IsCompleted)
            {
                var ready = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(10), deadline.Token));
                if (ready != all) System.Console.WriteLine(JsonSerializer.Serialize(new { state = "transferring", count,
                    mib_each = mib, received_bytes = sizes.Sum(), elapsed_seconds = watch.Elapsed.TotalSeconds }));
            }
            await all;
            if (breakOffset <= 0 || breakOffset >= size || linked[0] < 2) throw new IOException("Midstream reconnect was not observed.");
            var result = new { ok = true, simulated = true, transport = "real loopback TLS AgentLink/LabSession", count,
                bytes_each = size, total_bytes = size * count, elapsed_seconds = watch.Elapsed.TotalSeconds,
                mib_per_second = size * count / 1048576d / watch.Elapsed.TotalSeconds,
                sha256_verified_sinks = transfers.Length, resume_agent = 1, disconnect_after_bytes = breakOffset,
                resume_link_events = linked[0], append_only_sinks = true, source_sha256 = offer.Sha256 };
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            System.Console.WriteLine(json);
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "transfer.results.json"), json);
            return 0;
        }
        catch (Exception error) { System.Console.Error.WriteLine("FAIL transfer drill: " + error.GetType().Name); return 1; }
        finally
        {
            foreach (var link in links) await link.DisposeAsync();
            foreach (var store in stores) store.Dispose();
            CryptographicOperations.ZeroMemory(keyBytes);
            Directory.Delete(root, true);
        }
    }
    private static async Task WaitUntil(Func<bool> predicate, TimeSpan timeout, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate()) { if (watch.Elapsed > timeout) throw new TimeoutException(); await Task.Delay(25, token); }
    }
    private static byte[] Protect(byte[] key, byte[] input)
    {
        var output = new byte[input.Length + 28];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(output.AsSpan(0, 12), input, output.AsSpan(28), output.AsSpan(12, 16));
        return output;
    }
    private static byte[] Unprotect(byte[] key, byte[] input)
    {
        var output = new byte[input.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(input.AsSpan(0, 12), input.AsSpan(28), input.AsSpan(12, 16), output);
        return output;
    }
    private sealed class Behaviour : IAgentBehaviour
    {
        public void Describe(Hello hello) { hello.AgentVersion = "transfer-drill"; }
        public Inventory? DescribeInventory() => null;
        public Task<JobResult> RunJobAsync(Job job, Func<JobProgress, Task> report, CancellationToken token) =>
            Task.FromResult(new JobResult { JobId = job.Id, Ok = false, ExitCode = 1 });
    }
    private sealed class Sink(string path, Func<long, Task> afterWrite) : Stream
    {
        private readonly FileStream _file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        public long Written { get; private set; }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => Written;
        public override long Position { get => Written; set => throw new NotSupportedException(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { await _file.WriteAsync(buffer, token); Written += buffer.Length; await afterWrite(Written); }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => _file.Flush();
        public override async ValueTask DisposeAsync() { await _file.DisposeAsync(); GC.SuppressFinalize(this); }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DriveBench.Core;
using DriveBench.Host;

if (args.Length != 2 || !args[0].StartsWith("DriveBench-") || args[1] is not ("dry-run" or "vigem")) return 2;
string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveBench", "host.log");
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
void Log(string t) { try { File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} {t}{Environment.NewLine}"); } catch { } }
try
{
    await using var pipe = new NamedPipeServerStream(args[0], PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await pipe.WaitForConnectionAsync(lifetime.Token);
    lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
    using IBackend backend = args[1] == "vigem" ? new VigemBackend() : new DryRun();
    var lease = new InputLease(Stopwatch.Frequency);
    object gate = new();
    var receiver = Task.Run(async () =>
    {
        byte[] bytes = new byte[InputFrame.Size];
        try { while (!lifetime.IsCancellationRequested) { await pipe.ReadExactlyAsync(bytes, lifetime.Token); var f = InputFrame.Decode(bytes); lock (gate) lease.Accept(f, Stopwatch.GetTimestamp()); } }
        catch { lock (gate) lease.Disconnect(); lifetime.Cancel(); }
    });
    HostStatus latestStatus = default;
    var feedback = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(40));
        try { while (await timer.WaitForNextTickAsync(lifetime.Token)) { HostStatus s; lock (gate) s = latestStatus; if (s.CompletedTicks != 0) await pipe.WriteAsync(s.Encode(), lifetime.Token); } }
        catch { lifetime.Cancel(); }
    });
    try
    {
        using var outputTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(8));
        while (await outputTimer.WaitForNextTickAsync(lifetime.Token))
        {
            PadReport r; ulong seq; uint ep; LeaseState st;
            lock (gate) { r = lease.Sample(Stopwatch.GetTimestamp()); st = lease.State; ep = lease.Epoch; seq = st == LeaseState.Armed ? lease.CurrentSequence : 0; }
            backend.Submit(r); bool match = backend.ReadbackMatches(r); long now = Stopwatch.GetTimestamp();
            lock (gate) { lease.Submitted(seq); latestStatus = new(now, lease.LastAppliedSequence, ep, r, st, backend.Real, match); }
        }
    }
    catch (OperationCanceledException) { } finally { backend.Submit(default); lifetime.Cancel(); }
    return 0;
}
catch (Exception ex) { Log(ex.ToString()); return 1; }
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Services.Transport.Transports;
using MCPForUnityLauncher.Editor;

internal static class Program
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(100);
    private static int _passed;

    private static int Main()
    {
        try
        {
            using (var context = new EditorLikeContext())
                context.Run(Run);
            Console.WriteLine("TRANSPORT_LIFECYCLE_VERIFICATION: passed " + _passed + " scenarios (linked production code)");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task Run()
    {
        await Check("hung start and hung cleanup allow a fresh generation", HungStart);
        await Check("late old success is retired without overwriting the replacement", LateSuccess);
        await Check("failed, empty and hung probes cannot report Ready", ProbeFailures);
        await Check("cancelled registration cannot revive a stopped generation", StaleRegistration);
        await Check("late verification cannot overwrite a replacement", StaleVerification);
        await Check("upstream session changes require a fresh confirmation", SessionChange);
        await Check("disabling management preserves the existing connection", DisabledPreserves);
        await Check("scope changes cancel pending local work", ConfigurationChange);
        await Check("retirement uses the WebSocket force-stop path", SocketRetirement);
        await Check("recovering one adapter preserves another", ProjectIsolation);
    }

    private static async Task Check(string name, Func<Task> scenario)
    {
        await scenario();
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static ManagedHttpTransportClient Adapter(Func<IMcpTransportClient> factory,
        Func<CancellationToken, Task<string>> probe = null, Func<bool> managed = null)
        => new ManagedHttpTransportClient(factory, probe ?? (_ => Task.FromResult("confirmed")), managed ?? (() => true), Deadline);

    private static async Task HungStart()
    {
        var never = new TaskCompletionSource<bool>();
        var first = new FakeClient { Start = () => never.Task, Stop = () => never.Task };
        var second = new FakeClient();
        var queue = new Queue<FakeClient>(new[] { first, second });
        var adapter = Adapter(() => queue.Dequeue());
        Require(!await Timely(adapter.StartAsync()), "A hung start was accepted");
        Require(first.Stops > 0, "Timed-out client was not retired");
        Require(await Timely(adapter.StartAsync()), "A hung old start blocked the new generation");
        Require(first.Starts == 1 && second.Starts == 1 && queue.Count == 0, "A retired client was reused");
        await Timely(adapter.StopAsync());
        Require(!adapter.IsConnected, "Stop retained Ready");
    }

    private static async Task LateSuccess()
    {
        var completion = new TaskCompletionSource<bool>();
        var old = new FakeClient { Start = () => completion.Task };
        var current = new FakeClient();
        var queue = new Queue<FakeClient>(new[] { old, current });
        var adapter = Adapter(() => queue.Dequeue());
        Require(!await Timely(adapter.StartAsync()), "The old start did not time out");
        Require(await Timely(adapter.StartAsync()), "Replacement failed");
        int stopsBeforeLate = old.Stops;
        completion.SetResult(true);
        await Until(() => old.Stops > stopsBeforeLate);
        Require(!old.IsConnected && adapter.IsConnected && adapter.State.SessionId == "confirmed", "Late success contaminated the current generation");
        await adapter.StopAsync();
    }

    private static async Task ProbeFailures()
    {
        foreach (string failure in new[] { "empty", "exception", "never" })
        {
            var never = new TaskCompletionSource<string>();
            var raw = new FakeClient();
            var adapter = Adapter(() => raw, _ => failure == "empty" ? Task.FromResult<string>(null) :
                failure == "exception" ? Task.FromException<string>(new InvalidOperationException("Injected failure")) : never.Task);
            Require(!await Timely(adapter.StartAsync()), failure + " probe was accepted");
            Require(!adapter.IsConnected && !adapter.State.IsConnected && raw.Stops > 0, failure + " probe retained Ready");
            await adapter.StopAsync();
        }
        int probes = 0;
        var hungVerify = new TaskCompletionSource<string>();
        var verify = Adapter(() => new FakeClient(), _ => ++probes == 1 ? Task.FromResult("ready") : hungVerify.Task);
        Require(await Timely(verify.StartAsync()), "Verification fixture failed to start");
        Require(!await Timely(verify.VerifyAsync()) && !verify.IsConnected, "Hung verification retained Ready");
        await verify.StopAsync();
    }

    private static async Task StaleRegistration()
    {
        int probes = 0;
        var old = new TaskCompletionSource<string>();
        var adapter = Adapter(() => new FakeClient(), _ => ++probes == 1 ? old.Task : Task.FromResult("replacement"));
        Task<bool> first = adapter.StartAsync();
        await Until(() => probes == 1);
        await Timely(adapter.StopAsync());
        Require(await Timely(adapter.StartAsync()), "Replacement could not start after cancellation");
        old.SetResult("obsolete");
        Require(!await Timely(first), "Cancelled start reported success");
        Require(adapter.IsConnected && adapter.State.SessionId == "replacement", "Cancelled registration replaced the new session");
        await adapter.StopAsync();
    }

    private static async Task StaleVerification()
    {
        int probes = 0;
        var old = new TaskCompletionSource<string>();
        var adapter = Adapter(() => new FakeClient(), _ => ++probes == 2 ? old.Task : Task.FromResult("ready-" + probes));
        Require(await Timely(adapter.StartAsync()), "Verification fixture failed to start");
        Task<bool> verify = adapter.VerifyAsync();
        await Until(() => probes == 2);
        await adapter.StopAsync();
        Require(await Timely(adapter.StartAsync()), "Replacement could not start during stale verification");
        old.SetResult("obsolete-verify");
        Require(!await Timely(verify) && adapter.State.SessionId == "ready-3", "Old verification overwrote the new session");
        await adapter.StopAsync();
    }

    private static async Task SessionChange()
    {
        string session = "first";
        var raw = new FakeClient();
        var adapter = Adapter(() => raw, _ => Task.FromResult(session));
        Require(await Timely(adapter.StartAsync()), "Session fixture failed to start");
        raw.State = TransportState.Connected("fake", sessionId: "new-raw");
        Require(!adapter.IsConnected && !adapter.State.IsConnected, "A changed raw session reused the old confirmation");
        session = "second";
        Require(await Timely(adapter.VerifyAsync()) && adapter.State.SessionId == session, "New raw session was not confirmed");
        raw.IsConnected = false;
        Require(!adapter.IsConnected, "Raw socket failure retained Ready");
        raw.IsConnected = true;
        raw.State = TransportState.Connected("fake", sessionId: "pending");
        Require(!adapter.IsConnected, "A reconnecting raw socket was immediately Ready");
        session = "third";
        Require(await Timely(adapter.VerifyAsync()) && adapter.State.SessionId == session, "Reconnected socket failed to confirm");
        await adapter.StopAsync();
    }

    private static async Task DisabledPreserves()
    {
        bool managed = true;
        int probes = 0;
        var raw = new FakeClient();
        var adapter = Adapter(() => raw, _ => { probes++; return Task.FromResult("managed"); }, () => managed);
        Require(await Timely(adapter.StartAsync()), "Disable fixture failed to start");
        managed = false;
        int stops = raw.Stops;
        Require(adapter.IsConnected && adapter.State.SessionId == "raw", "Disable changed the upstream connection");
        Require(await Timely(adapter.VerifyAsync()) && probes == 1 && raw.Stops == stops, "Disable stopped the connection or ran a local registration probe");
        managed = true;
        Require(adapter.IsConnected && adapter.State.SessionId == "managed", "Disable erased the existing confirmation");
        await adapter.StopAsync();
    }

    private static async Task ConfigurationChange()
    {
        bool managed = true;
        int probes = 0;
        var oldProbe = new TaskCompletionSource<string>();
        var oldRaw = new FakeClient();
        var newRaw = new FakeClient();
        var clients = new Queue<FakeClient>(new[] { oldRaw, newRaw });
        var adapter = Adapter(() => clients.Dequeue(), _ => ++probes == 1 ? oldProbe.Task : Task.FromResult("new-local"), () => managed);
        Task<bool> obsolete = adapter.StartAsync();
        await Until(() => probes == 1);
        managed = false;
        adapter.CancelPendingLocalStart();
        Require(await Timely(adapter.StartAsync()), "New unmanaged configuration failed");
        oldProbe.SetResult("old-local");
        Require(!await Timely(obsolete) && adapter.State.SessionId == "raw" && !oldRaw.IsConnected && newRaw.IsConnected,
            "Late local registration contaminated an unmanaged replacement");
        await adapter.StopAsync();
    }

    private static async Task SocketRetirement()
    {
        var socket = new WebSocketTransportClient();
        var adapter = Adapter(() => socket);
        Require(await Timely(adapter.StartAsync()), "Socket fixture failed to start");
        await Timely(adapter.StopAsync());
        Require(socket.ForcedStops == 1 && socket.GracefulStops == 0 && !socket.IsConnected, "Retirement attempted a graceful WebSocket close");
    }

    private static async Task ProjectIsolation()
    {
        var aClients = new Queue<FakeClient>(new[] { new FakeClient(), new FakeClient() });
        var rawB = new FakeClient();
        var a = Adapter(() => aClients.Dequeue());
        var b = Adapter(() => rawB, _ => Task.FromResult("B-original"));
        Require(await Timely(a.StartAsync()) && await Timely(b.StartAsync()), "Independent project fixtures failed");
        await a.StopAsync();
        Require(await Timely(a.StartAsync()), "A did not recover");
        Require(b.IsConnected && b.State.SessionId == "B-original" && rawB.Starts == 1 && rawB.Stops == 0,
            "Recovering A changed B");
        await a.StopAsync();
        await b.StopAsync();
    }

    private static async Task Until(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition()) { Require(DateTime.UtcNow < deadline, "Asynchronous continuation did not settle"); await Task.Delay(1); }
    }
    private static async Task<T> Timely<T>(Task<T> task)
    {
        Require(await Task.WhenAny(task, Task.Delay(3000)) == task, "Lifecycle operation exceeded its test deadline");
        return await task;
    }
    private static async Task Timely(Task task)
    {
        Require(await Task.WhenAny(task, Task.Delay(3000)) == task, "Lifecycle operation exceeded its test deadline");
        await task;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeClient : IMcpTransportClient
    {
        public int Starts;
        public int Stops;
        public Func<Task<bool>> Start;
        public Func<Task> Stop;
        public string TransportName => "fake";
        public bool IsConnected { get; set; }
        public TransportState State { get; set; } = TransportState.Disconnected("fake");
        public async Task<bool> StartAsync()
        {
            Starts++;
            IsConnected = Start == null || await Start();
            State = IsConnected ? TransportState.Connected(TransportName, sessionId: "raw") : TransportState.Disconnected(TransportName);
            return IsConnected;
        }
        public Task StopAsync()
        {
            Stops++;
            IsConnected = false;
            State = TransportState.Disconnected(TransportName);
            return Stop == null ? Task.CompletedTask : Stop();
        }
        public Task<bool> VerifyAsync() => Task.FromResult(IsConnected);
        public Task ReregisterToolsAsync() => Task.CompletedTask;
    }

    // Unity resumes lifecycle awaits on its Editor thread. This pump keeps the
    // same contract without a license or concurrent state mutation in CI.
    private sealed class EditorLikeContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        public override void Post(SendOrPostCallback callback, object state) => _queue.Add(() => callback(state));
        public void Run(Func<Task> action)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                Task work = action();
                while (!work.IsCompleted)
                    if (_queue.TryTake(out var callback, 100)) callback();
                work.GetAwaiter().GetResult();
            }
            finally { SetSynchronizationContext(previous); }
        }
        public void Dispose() => _queue.Dispose();
    }
}

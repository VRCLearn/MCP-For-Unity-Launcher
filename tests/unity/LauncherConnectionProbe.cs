using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using UnityEditor;
using UnityEngine;

public static class LauncherConnectionProbe
{
    private static readonly Type Bootstrap = Type.GetType("MCPForUnityLauncher.Editor.LauncherBootstrap, MCPForUnityLauncher.Editor", true);
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;

    public static async Task Run()
    {
        string enabledKey = (string)Bootstrap.GetField("PreferenceKey", Flags).GetValue(null);
        string[] keys = { enabledKey, "MCPForUnity.UseHttpTransport", "MCPForUnity.HttpTransportScope" };
        bool[] existed = Array.ConvertAll(keys, EditorPrefs.HasKey);
        bool enabled = EditorPrefs.GetBool(keys[0], true);
        bool http = EditorPrefs.GetBool(keys[1], true);
        string scope = EditorPrefs.GetString(keys[2], "");
        var config = EditorConfigurationCache.Instance;
        try
        {
            EditorPrefs.SetBool(enabledKey, true);
            config.SetUseHttpTransport(true);
            config.SetHttpTransportScope("local");
            await VerifyBootstrap();
            await VerifyBoundedStartsAndLateCompletion();
            await VerifyReadinessFailures();
            await VerifyStoppedProbeCannotRevive();
            await VerifyUnmanagedTransport();
            await VerifySessionChangeAndDisabledManagement();
            await VerifyConfigurationTransitionCancellation();
            await VerifyServerReadinessGate();
            await VerifyNeverConnectedDoesNotPeriodicallyVerify();
            Debug.Log("LAUNCHER_CONNECTION_VERIFICATION: passed");
        }
        finally
        {
            MCPServiceLocator.TransportManager.ForceStop(TransportMode.Http);
            EditorPrefs.SetBool(keys[0], enabled);
            EditorPrefs.SetBool(keys[1], http);
            EditorPrefs.SetString(keys[2], scope);
            for (int i = 0; i < keys.Length; i++) if (!existed[i]) EditorPrefs.DeleteKey(keys[i]);
            config.Refresh();
            Invoke("RestoreWrapper");
            MCPServiceLocator.Reset();
        }
    }

    private static async Task VerifyBootstrap()
    {
        var client = new TestClient();
        var manager = new TransportManager();
        manager.Configure(() => client, () => new TestClient());
        // Do not replace a transport already supplied by another integration.
        await manager.StartAsync(TransportMode.Http);
        await manager.StopAsync(TransportMode.Http);
        client.Starts = 0;
        MCPServiceLocator.Register(manager);
        MCPServiceLocator.Register<IBridgeControlService>(new BridgeControlService());
        MCPServiceLocator.Register<IServerManagementService>(new UnreachableServer());
        Invoke("InstallWrapper");
        Set("_connectedOnce", false);
        Set("_needsNewEndpoint", false);
        Set("_nextConnect", 0d);
        Set("_disconnectedSince", -1d);
        Set("_nextVerify", double.MaxValue);

        await TickConnection();
        Require(client.Starts == 1 && client.IsConnected, "TCP probe prevented the actual connection");
        await TickConnection();
        Require(client.Starts == 1, "An established session was restarted");

        client.IsConnected = false;
        client.State = client.State.WithError("Socket closed; upstream is reconnecting");
        await TickConnection();
        Require(client.Starts == 1, "Launcher interrupted upstream reconnection before its grace period");
        Require((double)Get("_disconnectedSince") != -1d, "Disconnected project did not start its recovery timer");
        client.IsConnected = true;
        client.State = TransportState.Connected("test", sessionId: "self-recovered");
        await TickConnection();
        Require(client.Starts == 1, "An upstream recovery was replaced unnecessarily");
        Require((double)Get("_disconnectedSince") == -1d, "A recovered project retained a stale recovery timer");

        client.IsConnected = false;
        client.State = client.State.WithError("Reconnect task is no longer making progress");
        Set("_disconnectedSince", EditorApplication.timeSinceStartup - 61d);
        await TickConnection();
        Require(client.Starts == 2 && client.IsConnected, "Disconnected project did not recover after the grace period");

        await manager.StopAsync(TransportMode.Http);
        await TickConnection();
        Require(client.Starts == 3 && client.IsConnected, "Automatic management did not resume after Stop");

        await manager.StopAsync(TransportMode.Http);
        client.NextResult = false;
        await TickConnection();
        Require(client.Starts == 4 && !client.IsConnected, "Failed connection was not attempted");
        await TickConnection();
        Require(client.Starts == 4, "Failed connection ignored backoff");
        Set("_nextConnect", 0d);
        client.NextResult = true;
        await TickConnection();
        Require(client.Starts == 5 && client.IsConnected, "Failed connection was not retried");

        Set("_needsNewEndpoint", true);
        Set("_connectedOnce", false);
        await TickConnection();
        Require(client.Starts == 6, "Endpoint change retained the old session");
        Debug.Log("LAUNCHER_WATCHDOG_VERIFICATION: passed");
    }

    private static async Task VerifyBoundedStartsAndLateCompletion()
    {
        var lateStart = new TaskCompletionSource<bool>();
        var neverStop = new TaskCompletionSource<bool>();
        var first = new TestClient { OnStart = () => lateStart.Task, OnStop = () => neverStop.Task };
        var second = new TestClient();
        var clients = new Queue<TestClient>(new[] { first, second });
        var adapter = NewManaged(() => clients.Dequeue(), token => Task.FromResult("ready-session"));
        var manager = new TransportManager();
        manager.Configure(() => adapter, () => new TestClient());
        try
        {
            Require(!await Within(manager.StartAsync(TransportMode.Http), "Hung StartAsync did not time out"),
                "Hung StartAsync reported success");
            Require(first.Stops > 0, "Timed-out client was not retired");
            Require(await Within(manager.StartAsync(TransportMode.Http), "Manager retained the timed-out start task"),
                "A new client generation could not connect");
            Require(first.Starts == 1 && second.Starts == 1 && clients.Count == 0,
                "Recovery reused the retired transport instead of creating a new generation");
            lateStart.SetResult(true);
            await NextEditorUpdate();
            await NextEditorUpdate();
            Require(first.Stops >= 2 && !first.IsConnected, "Late old success was not retired again");
            Require(adapter.IsConnected && adapter.State.SessionId == "ready-session",
                "Late old success overwrote the current transport state");
            await Within(adapter.StopAsync(), "Hung StopAsync blocked shutdown");
            Require(!adapter.IsConnected, "Stopped adapter retained its ready state");
            Debug.Log("LAUNCHER_BOUNDED_TRANSPORT_VERIFICATION: passed");
        }
        finally { await adapter.StopAsync(); }
    }

    private static async Task VerifyReadinessFailures()
    {
        var empty = NewManaged(() => new TestClient(), token => Task.FromResult<string>(null));
        Require(!await Within(empty.StartAsync(), "Missing registration confirmation hung startup"),
            "Missing registration confirmation was accepted");
        Require(!empty.IsConnected && !empty.State.IsConnected, "An unconfirmed transport displayed Connected");
        await empty.StopAsync();

        var failed = NewManaged(() => new TestClient(), token => Task.FromException<string>(new Exception("Injected readiness failure")));
        Require(!await Within(failed.StartAsync(), "Failed readiness did not settle startup"),
            "Failed readiness was accepted");
        Require(!failed.IsConnected, "Failed readiness retained its connected state");
        await failed.StopAsync();

        int probes = 0;
        var neverVerify = new TaskCompletionSource<string>();
        var verify = NewManaged(() => new TestClient(), token => ++probes == 1
            ? Task.FromResult("initial-session") : neverVerify.Task);
        Require(await Within(verify.StartAsync(), "Readiness fixture could not start"), "Readiness fixture start failed");
        Require(!await Within(verify.VerifyAsync(), "Hung verification did not time out"),
            "Hung verification reported success");
        Require(!verify.IsConnected && !verify.State.IsConnected, "Failed verification retained Ready");
        await verify.StopAsync();
        Debug.Log("LAUNCHER_READINESS_VERIFICATION: passed");
    }

    private static async Task VerifyStoppedProbeCannotRevive()
    {
        int probes = 0;
        var oldProbe = new TaskCompletionSource<string>();
        var adapter = NewManaged(() => new TestClient(), token => ++probes == 1
            ? oldProbe.Task : Task.FromResult("replacement-session"));
        Task<bool> obsolete = adapter.StartAsync();
        await WaitUntil(() => probes == 1, "Readiness probe was not reached");
        await Within(adapter.StopAsync(), "Stop waited for the old readiness probe");
        Task<bool> current = adapter.StartAsync();
        Require(await Within(current, "New generation waited for an obsolete probe"), "Replacement generation failed");
        oldProbe.SetResult("obsolete-session");
        Require(!await Within(obsolete, "Cancelled generation did not settle"), "Stopped generation reported late success");
        Require(adapter.IsConnected && adapter.State.SessionId == "replacement-session",
            "An old registration result revived or replaced the stopped generation");
        await adapter.StopAsync();
        Debug.Log("LAUNCHER_STALE_PROBE_VERIFICATION: passed");
    }

    private static async Task VerifyUnmanagedTransport()
    {
        int probes = 0;
        var raw = new TestClient();
        var adapter = NewManaged(() => raw, token =>
        {
            probes++;
            return Task.FromResult("managed-session");
        }, () => false);
        Require(await Within(adapter.StartAsync(), "Unmanaged transport could not start"), "Unmanaged start failed");
        Require(adapter.IsConnected && adapter.State.SessionId == "raw-session", "Unmanaged transport state was changed");
        Require(await Within(adapter.VerifyAsync(), "Unmanaged verify could not complete"), "Unmanaged verify failed");
        Require(probes == 0, "Local readiness probe ran for an unmanaged transport");
        await adapter.StopAsync();
        Debug.Log("LAUNCHER_UNMANAGED_TRANSPORT_VERIFICATION: passed");
    }

    private static async Task VerifySessionChangeAndDisabledManagement()
    {
        bool managed = true;
        int probes = 0;
        string confirmed = "first-confirmed-session";
        var raw = new TestClient();
        var adapter = NewManaged(() => raw, token =>
        {
            probes++;
            return Task.FromResult(confirmed);
        }, () => managed);
        Require(await Within(adapter.StartAsync(), "Session transition fixture failed to start"), "Session transition fixture start failed");
        raw.State = TransportState.Connected("test", sessionId: "new-upstream-session");
        Require(!adapter.IsConnected && !adapter.State.IsConnected, "Changed upstream session retained the old Ready confirmation");
        confirmed = "new-confirmed-session";
        Require(await Within(adapter.VerifyAsync(), "Changed session verification did not complete"), "Changed session could not be confirmed");
        Require(adapter.IsConnected && adapter.State.SessionId == confirmed, "New session confirmation was not published");
        raw.IsConnected = false;
        raw.State = raw.State.WithError("Temporary disconnect");
        Require(!adapter.IsConnected, "Upstream disconnect retained Ready");
        raw.IsConnected = true;
        raw.State = TransportState.Connected("test", sessionId: "pending");
        Require(!adapter.IsConnected, "A newly reconnected socket was Ready before its round trip");
        confirmed = "reconnected-confirmed-session";
        Require(await Within(adapter.VerifyAsync(), "Reconnected session verification did not complete"), "Reconnected session could not be confirmed");
        int stops = raw.Stops;
        int completedProbes = probes;
        managed = false;
        Require(adapter.IsConnected && adapter.State.SessionId == "pending", "Disabling management stopped or changed the existing upstream transport");
        Require(await Within(adapter.VerifyAsync(), "Disabled management verification did not complete"), "Disabled transport could not verify normally");
        Require(raw.Stops == stops && probes == completedProbes, "Disabling management stopped the session or continued local probes");
        managed = true;
        Require(adapter.IsConnected && adapter.State.SessionId == confirmed, "Temporary management disable erased the existing confirmation");
        await adapter.StopAsync();
        Debug.Log("LAUNCHER_SESSION_CHANGE_VERIFICATION: passed");
    }

    private static async Task VerifyConfigurationTransitionCancellation()
    {
        foreach (string transition in new[] { "disabled", "remote", "endpoint" })
        {
            bool managed = true;
            int probes = 0;
            var oldProbe = new TaskCompletionSource<string>();
            var oldRaw = new TestClient();
            var replacementRaw = new TestClient();
            var clients = new Queue<TestClient>(new[] { oldRaw, replacementRaw });
            var adapter = NewManaged(() => clients.Dequeue(), token => ++probes == 1
                ? oldProbe.Task : Task.FromResult("new-endpoint-session"), () => managed);
            Task<bool> obsolete = adapter.StartAsync();
            await WaitUntil(() => probes == 1, "Configuration transition did not reach the pending probe");
            if (transition != "endpoint") managed = false;
            // This is the exact adapter cancellation boundary used by the Launcher
            // when disable, remote scope, or a local endpoint change supersedes a start.
            adapter.GetType().GetMethod("CancelPendingLocalStart", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(adapter, null);
            Require(await Within(adapter.StartAsync(), "Replacement configuration did not connect"), "Replacement configuration start failed");
            string replacementSession = transition == "endpoint" ? "new-endpoint-session" : "raw-session";
            oldProbe.SetResult("obsolete-" + transition + "-session");
            Require(!await Within(obsolete, "Superseded configuration did not settle"), "Superseded configuration reported success");
            Require(adapter.IsConnected && adapter.State.SessionId == replacementSession,
                "Late " + transition + " probe overwrote the replacement session");
            Require(!oldRaw.IsConnected && replacementRaw.IsConnected, "Configuration transition revived its old client");
            await adapter.StopAsync();
        }
        Debug.Log("LAUNCHER_CONFIGURATION_TRANSITION_VERIFICATION: passed");
    }
    private static async Task VerifyServerReadinessGate()
    {
        int created = 0;
        var gate = new TaskCompletionSource<bool>();
        var adapter = NewWaiting(() => { created++; return new TestClient(); }, token => gate.Task);
        Task<bool> pending = adapter.StartAsync();
        Require(created == 0 && !adapter.IsConnected && Waiting(adapter),
            "Native startup created a raw transport before server readiness");
        gate.SetResult(true);
        Require(await Within(pending, "Ready server did not release native startup") && created == 1 && !Waiting(adapter),
            "Native health gate did not release exactly one client");
        await adapter.StopAsync();

        created = 0;
        int waits = 0;
        var never = new TaskCompletionSource<bool>();
        adapter = NewWaiting(() => { created++; return new TestClient(); }, token =>
            ++waits == 1 ? never.Task : Task.FromResult(waits >= 3));
        Require(!await Within(adapter.StartAsync(), "Native health gate did not time out") && created == 0 && !Waiting(adapter),
            "Native timed-out readiness created a client or remained in flight");
        Require(!await Within(adapter.StartAsync(), "Native false health gate did not settle") && created == 0,
            "Native false readiness was accepted");
        Require(await Within(adapter.StartAsync(), "Native readiness could not retry") && created == 1,
            "Native readiness failure prevented a later connection");
        await adapter.StopAsync();

        created = 0;
        waits = 0;
        var oldGate = new TaskCompletionSource<bool>();
        adapter = NewWaiting(() => { created++; return new TestClient(); }, token =>
            ++waits == 1 ? oldGate.Task : Task.FromResult(true));
        Task<bool> obsolete = adapter.StartAsync();
        Require(Waiting(adapter) && created == 0, "Native old readiness gate was not pending");
        await Within(adapter.StopAsync(), "Native pending gate blocked Stop");
        Require(await Within(adapter.StartAsync(), "Native replacement readiness did not connect"), "Native replacement startup failed");
        oldGate.SetResult(true);
        Require(!await Within(obsolete, "Native cancelled health gate did not settle") && created == 1 && adapter.IsConnected,
            "Native late health response created an obsolete client");
        await adapter.StopAsync();

        created = 0;
        bool managed = true;
        var manualGate = new TaskCompletionSource<bool>();
        adapter = NewWaiting(() => { created++; return new TestClient(); }, token => manualGate.Task, () => managed);
        Task<bool> manualPending = adapter.StartAsync();
        Require(Waiting(adapter) && created == 0, "Native manual gate was not pending");
        managed = false;
        manualGate.SetResult(true);
        Require(!await Within(manualPending, "Changed native scope did not settle old health wait") && created == 0 && !Waiting(adapter),
            "Native local health wait created a raw client after management changed");
        Require(await Within(adapter.StartAsync(), "Native replacement unmanaged configuration did not connect") && created == 1,
            "Native manual gate failure blocked the replacement configuration");
        await adapter.StopAsync();

        int unmanagedWaits = 0;
        adapter = NewWaiting(() => new TestClient(), token =>
        {
            unmanagedWaits++;
            throw new Exception("Unmanaged startup invoked local health gating");
        }, () => false);
        Require(await Within(adapter.StartAsync(), "Native unmanaged startup did not complete") && unmanagedWaits == 0 && !Waiting(adapter),
            "Native unmanaged transport was blocked by local health gating");
        await adapter.StopAsync();
        Debug.Log("LAUNCHER_SERVER_READINESS_GATE_VERIFICATION: passed");
    }

    private static async Task VerifyNeverConnectedDoesNotPeriodicallyVerify()
    {
        int created = 0;
        int probes = 0;
        var never = new TaskCompletionSource<bool>();
        var adapter = NewWaiting(() => { created++; return new TestClient(); }, token => never.Task,
            probe: token => { probes++; return Task.FromResult("unexpected"); });
        var manager = new TransportManager();
        manager.Configure(() => adapter, () => new TestClient());
        MCPServiceLocator.Register(manager);
        MCPServiceLocator.Register<IBridgeControlService>(new BridgeControlService());
        MCPServiceLocator.Register<IServerManagementService>(new UnreachableServer());
        Invoke("InstallWrapper");
        Set("_connectedOnce", false);
        Set("_needsNewEndpoint", false);
        Set("_nextConnect", 0d);
        Set("_nextVerify", 0d);
        Set("_verificationInFlight", false);
        Set("_disconnectedSince", -1d);
        Set("_lastConnectionError", null);
        Set("_connectFailures", 0);
        await TickConnection();
        string failure = (string)Get("_lastConnectionError");
        Require(created == 0 && probes == 0 && !(bool)Get("_connectedOnce"),
            "First failed native startup created a raw client or marked the project connected");
        Require(!string.IsNullOrEmpty(failure) && failure.IndexOf("server did not become ready", StringComparison.OrdinalIgnoreCase) >= 0,
            "Native startup did not preserve its readiness timeout reason");
        Set("_nextConnect", EditorApplication.timeSinceStartup + 600d);
        Set("_nextVerify", 0d);
        await TickConnection();
        Require((double)Get("_nextVerify") == 0d && !(bool)Get("_verificationInFlight") && probes == 0,
            "A never-connected native project performed periodic verification");
        Require((string)Get("_lastConnectionError") == failure,
            "Premature verification replaced the initial startup failure reason");
        manager.ForceStop(TransportMode.Http);
        Debug.Log("LAUNCHER_NO_PREMATURE_VERIFICATION: passed");
    }

    private static bool Waiting(IMcpTransportClient client)
        => (bool)client.GetType().GetProperty("WaitingForServer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);

    private static IMcpTransportClient NewWaiting(Func<IMcpTransportClient> factory,
        Func<CancellationToken, Task<bool>> waitForServer, Func<bool> managed = null,
        Func<CancellationToken, Task<string>> probe = null)
    {
        Type type = Type.GetType("MCPForUnityLauncher.Editor.ManagedHttpTransportClient, MCPForUnityLauncher.Editor", true);
        return (IMcpTransportClient)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { factory, probe ?? (token => Task.FromResult("ready-session")),
                managed ?? (() => true), TimeSpan.FromMilliseconds(50), waitForServer }, null);
    }

    private static IMcpTransportClient NewManaged(Func<IMcpTransportClient> factory,
        Func<CancellationToken, Task<string>> probe, Func<bool> managed = null)
    {
        Type type = Type.GetType("MCPForUnityLauncher.Editor.ManagedHttpTransportClient, MCPForUnityLauncher.Editor", true);
        return (IMcpTransportClient)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { factory, probe, managed ?? (() => true), TimeSpan.FromMilliseconds(50) }, null);
    }

    private static async Task TickConnection()
    {
        Invoke("TryConnect");
        await WaitUntil(() => !(bool)Get("_connectInFlight"), "Launcher connection attempt did not settle");
    }

    private static async Task WaitUntil(Func<bool> condition, string message)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            Require(DateTime.UtcNow < deadline, message);
            await NextEditorUpdate();
        }
    }

    private static Task NextEditorUpdate()
    {
        var completion = new TaskCompletionSource<bool>();
        EditorApplication.CallbackFunction callback = null;
        callback = () =>
        {
            EditorApplication.update -= callback;
            completion.TrySetResult(true);
        };
        EditorApplication.update += callback;
        return completion.Task;
    }

    private static async Task<T> Within<T>(Task<T> task, string message)
    {
        Require(await Task.WhenAny(task, Task.Delay(2000)) == task, message);
        return await task;
    }

    private static async Task Within(Task task, string message)
    {
        Require(await Task.WhenAny(task, Task.Delay(2000)) == task, message);
        await task;
    }

    private static void Invoke(string name) => Bootstrap.GetMethod(name, Flags).Invoke(null, null);
    private static object Get(string name) => Bootstrap.GetField(name, Flags).GetValue(null);
    private static void Set(string name, object value) => Bootstrap.GetField(name, Flags).SetValue(null, value);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class TestClient : IMcpTransportClient
    {
        public int Starts;
        public int Stops;
        public bool NextResult = true;
        public Func<Task<bool>> OnStart;
        public Func<Task> OnStop;
        public bool IsConnected { get; set; }
        public string TransportName => "test";
        public TransportState State { get; set; } = TransportState.Disconnected("test");
        public async Task<bool> StartAsync()
        {
            Starts++;
            IsConnected = OnStart == null ? NextResult : await OnStart();
            State = IsConnected ? TransportState.Connected("test", sessionId: "raw-session") : TransportState.Disconnected("test", "Not ready");
            return IsConnected;
        }
        public Task StopAsync()
        {
            Stops++;
            IsConnected = false;
            State = TransportState.Disconnected("test");
            return OnStop == null ? Task.CompletedTask : OnStop();
        }
        public Task<bool> VerifyAsync() => Task.FromResult(IsConnected);
        public Task ReregisterToolsAsync() => Task.CompletedTask;
    }

    private sealed class UnreachableServer : IServerManagementService
    {
        public bool ClearUvxCache() => false;
        public bool StartLocalHttpServer(bool quiet = false) => false;
        public bool StopLocalHttpServer() => false;
        public bool StopManagedLocalHttpServer() => false;
        public bool IsLocalHttpServerRunning() => false;
        public bool IsLocalHttpServerReachable() => false;
        public bool IsLocalUrl() => true;
        public bool CanStartLocalServer() => false;
        public string GetLocalHttpServerLaunchLogPath() => null;
        public bool IsManagedServerLaunchProcessAlive() => false;
        public bool HasManagedServerLaunchHandle => false;
        public void LogLocalHttpServerLaunchFailure() { }
        public bool TryGetLocalHttpServerCommand(out string command, out string error)
        {
            command = error = null;
            return false;
        }
    }
}

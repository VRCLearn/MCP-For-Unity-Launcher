using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

// Runs only in the generated batch project. All registrations use its Library
// directory; no shared supervisor or real server is started by these checks.
public static class LauncherMaintenanceProbe
{
    private static readonly Type Bootstrap = Type.GetType("MCPForUnityLauncher.Editor.LauncherBootstrap, MCPForUnityLauncher.Editor", true);
    private static readonly Type ResultType = Bootstrap.GetNestedType("MaintenanceResult", BindingFlags.NonPublic);
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    public static async Task Run()
    {
        string[] fields = { "_maintenance", "_maintenanceCancellation", "_maintenanceGeneration", "_settings",
            "_leasePath", "_lastLeaseJson", "_lastReleaseJson", "_releaseQueued", "_processId", "_processStart", "_status",
            "_supervisorHealthy", "_lastError" };
        var saved = new Dictionary<string, object>();
        foreach (string field in fields) saved[field] = Get(field);
        var paths = MCPServiceLocator.Paths;
        var forbiddenPaths = new ForbiddenPaths();
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../Library/LauncherMaintenanceProbe"));
        Directory.CreateDirectory(root);
        try
        {
            MCPServiceLocator.Register<IPathResolverService>(forbiddenPaths);
            Set("_maintenance", null);
            Set("_maintenanceCancellation", null);
            Set("_maintenanceGeneration", 100);
            Set("_leasePath", Path.Combine(root, "isolated-editor.json"));
            Set("_processId", Process.GetCurrentProcess().Id);
            Set("_processStart", "123456");
            Set("_lastLeaseJson", null);
            Set("_lastReleaseJson", null);
            Set("_releaseQueued", false);
            VerifySettingsCache();
            await VerifySingleWorkerAndOrderedRelease();
            await VerifyReenabledReleaseCannotBeSkipped();
            await VerifyActiveSchedulingInvalidatesCompletedRelease();
            VerifyStaleResultIsIgnored();
            Require(forbiddenPaths.Calls == 0, "Launcher called the synchronous executable-probing path service");
            Debug.Log("LAUNCHER_MAINTENANCE_VERIFICATION: passed");
        }
        finally
        {
            var pending = Get("_maintenance") as Task;
            if (pending != null) await Within(pending, "Maintenance cleanup did not settle");
            foreach (var entry in saved) Set(entry.Key, entry.Value);
            MCPServiceLocator.Register(paths);
        }
    }

    private static void VerifySettingsCache()
    {
        string[] strings = { "MCPForUnity.UvxPath", "MCPForUnity.GitUrlOverride" };
        string[] booleans = { "MCPForUnity.DevModeForceServerRefresh", "MCPForUnity.ProjectScopedTools.LocalHttp" };
        var existed = new Dictionary<string, bool>();
        var oldStrings = new Dictionary<string, string>();
        var oldBooleans = new Dictionary<string, bool>();
        foreach (string key in strings) { existed[key] = EditorPrefs.HasKey(key); oldStrings[key] = EditorPrefs.GetString(key); }
        foreach (string key in booleans) { existed[key] = EditorPrefs.HasKey(key); oldBooleans[key] = EditorPrefs.GetBool(key); }
        try
        {
            Set("_settings", null);
            EditorPrefs.SetString(strings[0], Path.Combine(Application.dataPath, "fake-uvx"));
            EditorPrefs.SetString(strings[1], "mcpforunityserver==10.3.0");
            EditorPrefs.SetBool(booleans[0], false);
            EditorPrefs.SetBool(booleans[1], true);
            object first = Invoke("CaptureSettings");
            Require(ReferenceEquals(first, Invoke("CaptureSettings")), "Unchanged launch settings were not cached");
            Require(Array.IndexOf(Arguments(first), "--offline") < 0, "Cached arguments included an automatic offline probe flag");

            EditorPrefs.SetString(strings[0], Path.Combine(Application.dataPath, "another-fake-uvx"));
            object changedPath = Invoke("CaptureSettings");
            Require(!ReferenceEquals(first, changedPath), "Executable override did not invalidate launch settings");
            EditorPrefs.SetString(strings[1], "mcpforunityserver==10.3.1");
            object changedSource = Invoke("CaptureSettings");
            Require(!ReferenceEquals(changedPath, changedSource) && Array.IndexOf(Arguments(changedSource), "mcpforunityserver==10.3.1") >= 0,
                "Server package source did not invalidate launch arguments");
            EditorPrefs.SetBool(booleans[0], true);
            object changedRefresh = Invoke("CaptureSettings");
            Require(!ReferenceEquals(changedSource, changedRefresh) && Arguments(changedRefresh).Length > Arguments(changedSource).Length,
                "Explicit server refresh did not invalidate launch arguments");
            EditorPrefs.SetBool(booleans[1], false);
            object changedScope = Invoke("CaptureSettings");
            Require(!ReferenceEquals(changedRefresh, changedScope) && Array.IndexOf(Arguments(changedScope), "--project-scoped-tools") < 0,
                "Tool scope did not invalidate launch arguments");
            Require(ReferenceEquals(changedScope, Invoke("CaptureSettings")), "Final unchanged settings were rebuilt");
        }
        finally
        {
            foreach (string key in strings) EditorPrefs.SetString(key, oldStrings[key]);
            foreach (string key in booleans) EditorPrefs.SetBool(key, oldBooleans[key]);
            foreach (var entry in existed) if (!entry.Value) EditorPrefs.DeleteKey(entry.Key);
            Set("_settings", null);
        }
        Debug.Log("LAUNCHER_SETTINGS_CACHE_VERIFICATION: passed");
    }

    private static async Task VerifySingleWorkerAndOrderedRelease()
    {
        string path = (string)Get("_leasePath");
        File.WriteAllText(path, "previous active lease");
        object settings = Invoke("CaptureSettings");
        // Resolve to an existing file without executing it. WriteLease only records it.
        SetInstance(settings, "Executable", typeof(LauncherMaintenanceProbe).Assembly.Location);
        int generation = (int)Get("_maintenanceGeneration");
        var entered = new TaskCompletionSource<bool>();
        using (var gate = new ManualResetEventSlim(false))
        {
            var cancellation = new CancellationTokenSource();
            Set("_maintenanceCancellation", cancellation);
            Task active = StartWorker(() =>
            {
                entered.TrySetResult(true);
                Require(gate.Wait(3000), "Simulated slow filesystem did not receive its cleanup signal");
                // Simulate an active write that had already passed cancellation when
                // release was requested. The queued tombstone must still win.
                Bootstrap.GetMethod("WriteLease", StaticFlags).Invoke(null, new[] { settings, (object)CancellationToken.None });
                return NewResult(generation);
            });
            Set("_maintenance", active);
            try
            {
                await Within(entered.Task, "Simulated background worker did not start");
                var elapsed = Stopwatch.StartNew();
                Invoke("QueueMaintenance");
                Invoke("QueueMaintenance");
                Require(elapsed.ElapsedMilliseconds < 200 && ReferenceEquals(active, Get("_maintenance")),
                    "A pending maintenance worker blocked the Editor or allowed overlapping workers");
                await NextEditorUpdate();
                Require(!active.IsCompleted, "The simulated slow worker did not span an Editor update");

                elapsed.Restart();
                Invoke("RemoveLease");
                Task release = (Task)Get("_maintenance");
                Require(elapsed.ElapsedMilliseconds < 200 && !ReferenceEquals(active, release) && !release.IsCompleted,
                    "Unregistering waited for I/O or did not queue behind the active write");
                Require(File.ReadAllText(path) == "previous active lease", "A tombstone was written before the active write finished");
                gate.Set();
                await Within(release, "Ordered lease release did not finish");
                string json = File.ReadAllText(path);
                Require(json.Contains("\"released\": true") && !json.Contains("\"released\": false"),
                    "A late active lease overwrote the release tombstone");
                Invoke("CompleteMaintenance");
                Require(Get("_lastLeaseJson") == null && Get("_lastReleaseJson") != null,
                    "A completed release lost its cache marker or retained an active lease marker");
                Invoke("RemoveLease");
                Require(Get("_maintenance") == null, "An already completed release scheduled duplicate filesystem work");
            }
            finally
            {
                gate.Set();
                var pending = Get("_maintenance") as Task;
                if (pending != null) await Within(pending, "Lease worker cleanup did not settle");
                Invoke("CompleteMaintenance");
            }
        }
        Debug.Log("LAUNCHER_SERIAL_MAINTENANCE_VERIFICATION: passed");
    }

    private static async Task VerifyReenabledReleaseCannotBeSkipped()
    {
        string path = (string)Get("_leasePath");
        string oldRelease = File.ReadAllText(path);
        Require(Get("_lastReleaseJson") != null && !(bool)Get("_releaseQueued"),
            "Repeated-disable fixture did not begin with a completed release");
        int generation = (int)Get("_maintenanceGeneration");
        var entered = new TaskCompletionSource<bool>();
        using (var gate = new ManualResetEventSlim(false))
        {
            Set("_maintenanceCancellation", new CancellationTokenSource());
            Task active = StartWorker(() =>
            {
                entered.TrySetResult(true);
                Require(gate.Wait(3000), "Reenabled active writer did not receive its cleanup signal");
                File.WriteAllText(path, "late reenabled active lease");
                Set("_lastLeaseJson", "late reenabled active lease");
                Set("_lastReleaseJson", null);
                return NewResult(generation);
            });
            Set("_maintenance", active);
            try
            {
                await Within(entered.Task, "Reenabled worker did not start");
                // The old completed-release cache still exists while the newly
                // enabled writer has not reached its first filesystem write.
                Invoke("RemoveLease");
                Task release = (Task)Get("_maintenance");
                Require(!ReferenceEquals(active, release) && (bool)Get("_releaseQueued"),
                    "A completed release marker suppressed unregistering a newly enabled pending writer");
                Invoke("RemoveLease");
                Require(ReferenceEquals(release, Get("_maintenance")), "Repeated disable duplicated a pending release task");
                Require(File.ReadAllText(path) == oldRelease, "A repeated release raced ahead of its active writer");

                // A further configuration change invalidates the result, but
                // must not leave the pending-release guard stuck forever.
                Invoke("CancelMaintenance");
                gate.Set();
                await Within(release, "Repeated-disable release did not finish");
                Invoke("CompleteMaintenance");
                Require(File.ReadAllText(path).Contains("\"released\": true") && Get("_lastLeaseJson") == null,
                    "A reenabled writer revived the lease after the second disable");
                Require(!(bool)Get("_releaseQueued"), "An obsolete release result left the release-in-flight guard set");
            }
            finally
            {
                gate.Set();
                var pending = Get("_maintenance") as Task;
                if (pending != null) await Within(pending, "Repeated-disable worker cleanup did not settle");
                Invoke("CompleteMaintenance");
            }
        }
        Debug.Log("LAUNCHER_REENABLE_RELEASE_VERIFICATION: passed");
    }

    private static async Task VerifyActiveSchedulingInvalidatesCompletedRelease()
    {
        object settings = Invoke("CaptureSettings");
        string executable = (string)settings.GetType().GetField("Executable", InstanceFlags).GetValue(settings);
        string uvxOverride = (string)settings.GetType().GetField("UvxOverride", InstanceFlags).GetValue(settings);
        string missing = Path.Combine(Application.dataPath, "LauncherMissingExecutable", "uvx");
        Require(!File.Exists(missing) && Get("_lastReleaseJson") != null,
            "Active-scheduling fixture did not begin with a release and an absent executable");
        try
        {
            SetInstance(settings, "Executable", null);
            SetInstance(settings, "UvxOverride", missing);
            Invoke("QueueMaintenance");
            // The worker cannot resolve this executable and therefore cannot
            // clear the cache in WriteLease. Scheduling itself must clear it.
            Require(Get("_lastReleaseJson") == null,
                "Reenabling did not invalidate the completed-release marker before background work began");
            await Within((Task)Get("_maintenance"), "Missing executable maintenance did not settle");
            Invoke("CompleteMaintenance");
        }
        finally
        {
            var pending = Get("_maintenance") as Task;
            if (pending != null) await Within(pending, "Active-scheduling cleanup did not settle");
            Invoke("CompleteMaintenance");
            SetInstance(settings, "Executable", executable);
            SetInstance(settings, "UvxOverride", uvxOverride);
        }
        Debug.Log("LAUNCHER_RELEASE_CACHE_INVALIDATION: passed");
    }

    private static void VerifyStaleResultIsIgnored()
    {
        int generation = (int)Get("_maintenanceGeneration");
        object result = NewResult(generation - 1);
        SetInstance(result, "Error", "Obsolete background failure");
        SetInstance(result, "Healthy", true);
        Set("_lastError", "current configuration state");
        Set("_supervisorHealthy", false);
        Set("_maintenance", CompletedWorker(result));
        Invoke("CompleteMaintenance");
        Require((string)Get("_lastError") == "current configuration state" && !(bool)Get("_supervisorHealthy"),
            "An old-generation worker overwrote the current configuration state");
        Debug.Log("LAUNCHER_STALE_MAINTENANCE_VERIFICATION: passed");
    }

    private static string[] Arguments(object settings) => (string[])settings.GetType().GetField("Arguments", InstanceFlags).GetValue(settings);
    private static object NewResult(int generation)
    {
        object result = Activator.CreateInstance(ResultType, true);
        SetInstance(result, "Generation", generation);
        return result;
    }
    private static Task StartWorker(Func<object> action) => (Task)typeof(LauncherMaintenanceProbe)
        .GetMethod("Worker", StaticFlags).MakeGenericMethod(ResultType).Invoke(null, new object[] { action });
    private static Task<T> Worker<T>(Func<object> action) => Task.Run(() => (T)action());
    private static Task CompletedWorker(object value) => (Task)typeof(Task).GetMethod("FromResult")
        .MakeGenericMethod(ResultType).Invoke(null, new[] { value });
    private static void SetInstance(object target, string name, object value) => target.GetType().GetField(name, InstanceFlags).SetValue(target, value);
    private static object Invoke(string name) => Bootstrap.GetMethod(name, StaticFlags).Invoke(null, null);
    private static object Get(string name) => Bootstrap.GetField(name, StaticFlags).GetValue(null);
    private static void Set(string name, object value) => Bootstrap.GetField(name, StaticFlags).SetValue(null, value);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Within(Task task, string message)
    {
        Require(await Task.WhenAny(task, Task.Delay(5000)) == task, message);
        await task;
    }
    private static Task NextEditorUpdate()
    {
        var completion = new TaskCompletionSource<bool>();
        EditorApplication.CallbackFunction callback = null;
        callback = () => { EditorApplication.update -= callback; completion.TrySetResult(true); };
        EditorApplication.update += callback;
        return completion.Task;
    }

    private sealed class ForbiddenPaths : IPathResolverService
    {
        internal int Calls;
        public string GetUvxPath() { Calls++; throw new Exception("Synchronous uvx path probing is forbidden in Launcher maintenance"); }
        public bool TryValidateUvxExecutable(string path, out string version) { Calls++; version = null; throw new Exception("Synchronous executable validation is forbidden"); }
        public string GetClaudeCliPath() => null;
        public bool IsPythonDetected() => false;
        public bool IsClaudeCliDetected() => false;
        public void SetUvxPathOverride(string path) { }
        public void SetClaudeCliPathOverride(string path) { }
        public void ClearUvxPathOverride() { }
        public void ClearClaudeCliPathOverride() { }
        public bool HasUvxPathOverride => false;
        public bool HasClaudeCliPathOverride => false;
        public bool HasUvxPathFallback => false;
    }
}

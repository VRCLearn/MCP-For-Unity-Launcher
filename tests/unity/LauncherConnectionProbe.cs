using System;
using System.Reflection;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using UnityEditor;
using UnityEngine;

public static class LauncherConnectionProbe
{
    private static readonly Type Bootstrap = Type.GetType("MCPForUnityLauncher.Editor.LauncherBootstrap, MCPForUnityLauncher.Editor", true);
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;

    public static void Run()
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
            var client = new TestClient();
            var manager = new TransportManager();
            manager.Configure(() => client, () => new TestClient());
            MCPServiceLocator.Register(manager);
            MCPServiceLocator.Register<IBridgeControlService>(new BridgeControlService());
            MCPServiceLocator.Register<IServerManagementService>(new UnreachableServer());
            Invoke("InstallWrapper");
            Set("_connectedOnce", false);
            Set("_needsNewEndpoint", false);
            Set("_nextConnect", 0d);

            Invoke("TryConnect");
            Require(client.Starts == 1 && client.IsConnected, "TCP probe prevented the actual connection");
            Invoke("TryConnect");
            Require(client.Starts == 1, "An established session was restarted");

            client.IsConnected = false;
            client.State = client.State.WithError("Socket closed; upstream is reconnecting");
            Invoke("TryConnect");
            Require(client.Starts == 1, "Launcher interrupted upstream reconnection");

            manager.StopAsync(TransportMode.Http).GetAwaiter().GetResult();
            Invoke("TryConnect");
            Require(client.Starts == 2 && client.IsConnected, "Automatic management did not resume after Stop");

            manager.StopAsync(TransportMode.Http).GetAwaiter().GetResult();
            client.NextResult = false;
            Invoke("TryConnect");
            Require(client.Starts == 3 && !client.IsConnected, "Failed connection was not attempted");
            Invoke("TryConnect");
            Require(client.Starts == 3, "Failed connection ignored backoff");
            Set("_nextConnect", 0d);
            client.NextResult = true;
            Invoke("TryConnect");
            Require(client.Starts == 4 && client.IsConnected, "Failed connection was not retried");

            Set("_needsNewEndpoint", true);
            Set("_connectedOnce", false);
            Invoke("TryConnect");
            Require(client.Starts == 5, "Endpoint change retained the old session");
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

    private static void Invoke(string name) => Bootstrap.GetMethod(name, Flags).Invoke(null, null);
    private static void Set(string name, object value) => Bootstrap.GetField(name, Flags).SetValue(null, value);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class TestClient : IMcpTransportClient
    {
        public int Starts;
        public bool NextResult = true;
        public bool IsConnected { get; set; }
        public string TransportName => "test";
        public TransportState State { get; set; } = TransportState.Disconnected("test");
        public Task<bool> StartAsync()
        {
            Starts++;
            IsConnected = NextResult;
            State = IsConnected ? TransportState.Connected("test") : TransportState.Disconnected("test", "Not ready");
            return Task.FromResult(IsConnected);
        }
        public Task StopAsync()
        {
            IsConnected = false;
            State = TransportState.Disconnected("test");
            return Task.CompletedTask;
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

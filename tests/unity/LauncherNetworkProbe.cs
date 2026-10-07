using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using UnityEditor;
using UnityEngine;

// Used only in generated batch projects. Never runs the supervisor or publishes a lease.
public static class LauncherNetworkProbe
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type Bootstrap = Type.GetType("MCPForUnityLauncher.Editor.LauncherBootstrap, MCPForUnityLauncher.Editor", true);

    public static async void Run()
    {
        string control = Environment.GetEnvironmentVariable("MCP_LAUNCHER_CONTROL_DIR");
        string label = Environment.GetEnvironmentVariable("MCP_LAUNCHER_PROJECT_LABEL");
        string url = Environment.GetEnvironmentVariable("MCP_LAUNCHER_BASE_URL");
        string prefix = Environment.GetEnvironmentVariable("MCP_LAUNCHER_PREF_PREFIX");
        int exitCode = 9;
        string[] keys = null;
        bool[] existed = null;
        bool[] booleans = null;
        string[] values = null;
        var config = EditorConfigurationCache.Instance;
        try
        {
            Require(!string.IsNullOrEmpty(control) && (label == "A" || label == "B") &&
                !string.IsNullOrEmpty(prefix) && prefix.StartsWith("MCPLauncher.NetworkTest."), "Explicit isolated network configuration is required");
            Require(Uri.TryCreate(url, UriKind.Absolute, out var endpoint) && endpoint.Host == "127.0.0.1" &&
                endpoint.Port != 8080 && endpoint.Scheme == "http", "Network probe requires an isolated loopback endpoint");
            Require(!EditorUtility.scriptCompilationFailed, "Network project failed to compile");
            // Keep this wrapper installed through upstream quit callbacks, which otherwise
            // read a user-wide server ownership handshake.
            Invoke("InstallWrapper");
            string enabledKey = (string)Bootstrap.GetField("PreferenceKey", Flags).GetValue(null);
            keys = new[] { enabledKey, prefix + "UseHttpTransport", prefix + "HttpTransportScope", prefix + "HttpUrl", prefix + "SessionId" };
            existed = Array.ConvertAll(keys, EditorPrefs.HasKey);
            booleans = new[] { EditorPrefs.GetBool(keys[0], true), EditorPrefs.GetBool(keys[1], true) };
            values = new[] { EditorPrefs.GetString(keys[2], ""), EditorPrefs.GetString(keys[3], ""), EditorPrefs.GetString(keys[4], "") };
            EditorPrefs.SetBool(enabledKey, true);
            config.SetUseHttpTransport(true);
            config.SetHttpTransportScope("local");
            config.SetHttpBaseUrl(url);
            Invoke("InstallTransport");
            var manager = MCPServiceLocator.TransportManager;
            string initial = null;
            string beforeRestart = null;
            bool retired = false;
            bool stable = false;
            bool restarted = false;
            DateTime deadline = DateTime.UtcNow.AddMinutes(12);
            while (!File.Exists(Path.Combine(control, label + ".finish")))
            {
                Require(DateTime.UtcNow < deadline, "Network recovery scenario exceeded its deadline");
                Invoke("TryConnect");
                var client = manager.GetClient(TransportMode.Http);
                string session = client != null && client.IsConnected ? client.State.SessionId : null;
                if (!string.IsNullOrEmpty(session) && session != "pending")
                {
                    if (initial == null)
                    {
                        Require(await manager.VerifyAsync(TransportMode.Http), "Initial project round trip failed");
                        initial = session;
                        beforeRestart = session;
                        Record(control, label, "ready", session);
                    }
                    if (retired && !File.Exists(Path.Combine(control, label + ".recovered.json")) && session != initial)
                    {
                        Require(await manager.VerifyAsync(TransportMode.Http), "Retired project failed registration or its round trip");
                        beforeRestart = session;
                        Record(control, label, "recovered", session);
                    }
                    if (label == "B" && !stable && File.Exists(Path.Combine(control, "B.check")))
                    {
                        Require(session == initial, "Recovering A interrupted B's existing session");
                        Require(await manager.VerifyAsync(TransportMode.Http), "B lost its tools or round trip while A recovered");
                        stable = true;
                        Record(control, label, "stable", session);
                    }
                    if (!restarted && File.Exists(Path.Combine(control, "server-restarted")) && session != beforeRestart)
                    {
                        // The adapter verifies registration, enabled tools, and a command
                        // round trip. A fresh socket alone cannot produce this result.
                        if (await manager.VerifyAsync(TransportMode.Http))
                        {
                            restarted = true;
                            Record(control, label, "server-recovered", manager.GetClient(TransportMode.Http).State.SessionId);
                        }
                    }
                }
                if (label == "A" && initial != null && !retired && File.Exists(Path.Combine(control, "A.retire")))
                {
                    manager.ForceStop(TransportMode.Http);
                    retired = true;
                }
                await Task.Delay(100);
            }
            Require(initial != null && restarted && (label == "A" ? retired : stable), "Network scenario was not completed");
            Debug.Log("LAUNCHER_NETWORK_VERIFICATION: passed " + label);
            exitCode = 0;
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            if (!string.IsNullOrEmpty(control) && !string.IsNullOrEmpty(label))
                File.WriteAllText(Path.Combine(control, label + ".failed.txt"), exception.ToString());
        }
        finally
        {
            MCPServiceLocator.TransportManager.ForceStop(TransportMode.Http);
            if (keys != null)
            {
                EditorPrefs.SetBool(keys[0], booleans[0]);
                EditorPrefs.SetBool(keys[1], booleans[1]);
                for (int i = 2; i < keys.Length; i++) EditorPrefs.SetString(keys[i], values[i - 2]);
                for (int i = 0; i < keys.Length; i++) if (!existed[i]) EditorPrefs.DeleteKey(keys[i]);
                config.Refresh();
            }
            // Deliberately retain the server-management decorator until process exit.
            EditorApplication.Exit(exitCode);
        }
    }

    private static void Record(string control, string label, string phase, string session)
    {
        string destination = Path.Combine(control, label + "." + phase + ".json");
        string temporary = destination + ".tmp";
        File.WriteAllText(temporary, JsonUtility.ToJson(new Result
        {
            project = label, phase = phase, session_id = session,
            project_path = Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
            verified_utc = DateTimeOffset.UtcNow.ToString("o")
        }, true));
        File.Move(temporary, destination);
        Debug.Log("LAUNCHER_NETWORK_STAGE: " + label + " " + phase + " " + session);
    }

    private static void Invoke(string name) => Bootstrap.GetMethod(name, Flags).Invoke(null, null);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    [Serializable]
    private sealed class Result
    {
        public string project;
        public string phase;
        public string session_id;
        public string project_path;
        public string verified_utc;
    }
}

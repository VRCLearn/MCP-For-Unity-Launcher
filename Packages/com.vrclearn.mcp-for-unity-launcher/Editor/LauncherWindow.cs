using System.IO;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using UnityEditor;
using UnityEngine;
using static MCPForUnityLauncher.Editor.LauncherLocalization;

namespace MCPForUnityLauncher.Editor
{
    internal sealed class LauncherWindow : EditorWindow
    {
        private Vector2 _scroll;

        [MenuItem("Window/MCP for Unity Launcher")]
        private static void Open()
        {
            var window = GetWindow<LauncherWindow>("MCP Launcher");
            window.minSize = new Vector2(440, 340);
            window.Show();
        }

        private void OnEnable() => LanguageChanged += Repaint;
        private void OnDisable() => LanguageChanged -= Repaint;
        private void OnInspectorUpdate() => Repaint();

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("MCP for Unity Launcher", EditorStyles.boldLabel);
            DrawLanguageSelector();
            EditorGUILayout.Space();
            bool enabled = EditorGUILayout.ToggleLeft(Text("Automatically manage MCP for this project"), LauncherBootstrap.Enabled);
            if (enabled != LauncherBootstrap.Enabled) LauncherBootstrap.Enabled = enabled;
            EditorGUILayout.HelpBox(Text("Opening a project starts the server and connects automatically. The shared server stays running while another managed Editor is open, and recovers after unexpected interruptions."), MessageType.Info);

            if (LauncherBootstrap.IsRemote)
                EditorGUILayout.HelpBox(Text("HTTP Remote is selected. Launcher keeps your remote settings and does not start a local server."), MessageType.Info);
            else
                EditorGUILayout.SelectableLabel(Format("MCP address: {0}", LauncherBootstrap.BaseUrl.TrimEnd('/') + "/mcp"), GUILayout.Height(20));

            var client = MCPServiceLocator.TransportManager.GetClient(TransportMode.Http);
            EditorGUILayout.LabelField(Text("Project connection"), client != null && client.IsConnected ? Text("Connected") :
                LauncherBootstrap.CanManageLocalServer ? Text("Disconnected / waiting for automatic connection") : Text("Disconnected"));
            if (!string.IsNullOrEmpty(client?.State?.SessionId) && client.State.SessionId != "pending")
                EditorGUILayout.LabelField(Text("Session ID"), client.State.SessionId);

            if (!string.IsNullOrEmpty(LauncherBootstrap.LastError))
                EditorGUILayout.HelpBox(LauncherBootstrap.LastError, MessageType.Warning);

            var status = LauncherBootstrap.ReadStatus();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Text("Shared supervisor"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(LauncherBootstrap.IsSupervisorHealthy ? Text("Running") : Text("Not running / waiting for recovery"));
            if (status != null)
            {
                EditorGUILayout.LabelField("PID", status.supervisorPid.ToString());
                EditorGUILayout.LabelField(Text("Last updated"), status.updatedUtc ?? "");
                EditorGUILayout.Space();
                EditorGUILayout.LabelField(Text("Registered Editors"), EditorStyles.boldLabel);
                if (status.editors != null)
                {
                    foreach (var editor in status.editors)
                    {
                        if (editor == null) continue;
                        EditorGUILayout.LabelField("PID " + editor.processId, Path.GetFileName(editor.projectPath ?? ""));
                        EditorGUILayout.SelectableLabel(editor.projectPath ?? "", GUILayout.Height(18));
                    }
                }
                EditorGUILayout.Space();
                EditorGUILayout.LabelField(Text("Services"), EditorStyles.boldLabel);
                if (status.services != null)
                {
                    foreach (var service in status.services)
                    {
                        if (service == null) continue;
                        EditorGUILayout.SelectableLabel(service.baseUrl ?? "", GUILayout.Height(18));
                        EditorGUILayout.LabelField(Text("Status"), TranslateState(service.state));
                        EditorGUILayout.LabelField(Text("Management"), service.owned ? Text("Managed by Launcher") : Text("Using an existing server"));
                        if (service.pid > 0) EditorGUILayout.LabelField("PID", service.pid.ToString());
                        if (service.restartCount > 0) EditorGUILayout.LabelField(Text("Recovery count"), service.restartCount.ToString());
                        if (!string.IsNullOrEmpty(service.error)) EditorGUILayout.HelpBox(service.error, MessageType.Warning);
                    }
                }
            }
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Text("Log directory"), EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(LauncherBootstrap.LogDirectory, GUILayout.Height(36));
            if (GUILayout.Button(Text("Open log directory")))
            {
                Directory.CreateDirectory(LauncherBootstrap.LogDirectory);
                EditorUtility.RevealInFinder(LauncherBootstrap.LogDirectory);
            }
            EditorGUILayout.EndScrollView();
        }

        private static string TranslateState(string state)
        {
            switch (state)
            {
                case "running":
                case "healthy": return Text("Running");
                case "starting": return Text("Starting");
                case "external": return Text("Existing server");
                case "retrying":
                case "backoff": return Text("Waiting for automatic recovery");
                case "unhealthy": return Text("Unhealthy / checking");
                case "blocked": return Text("Port occupied by another application");
                case "conflict": return Text("Projects have conflicting server settings");
                case "waiting": return Text("Waiting to start");
                case "idle": return Text("Waiting for the last Editor to close");
                case "error": return Text("Error");
                case "stopped": return Text("Stopped");
                default: return state ?? Text("Waiting to start");
            }
        }
    }
}

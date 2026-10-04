using UnityEditor;
using UnityEngine;
using static MCPForUnityLauncher.Editor.LauncherLocalization;

namespace MCPForUnityLauncher.Editor
{
    [InitializeOnLoad]
    internal static class LauncherDependencyPrompt
    {
        private const string PromptKey = "MCPForUnityLauncher.DependencyPromptShown";

        static LauncherDependencyPrompt()
        {
#if MCP_FOR_UNITY_LAUNCHER_HAS_MCP
            SessionState.SetBool(PromptKey, false);
#else
            if (!Application.isBatchMode) EditorApplication.update += CheckOnce;
#endif
        }

#if !MCP_FOR_UNITY_LAUNCHER_HAS_MCP
        private static void CheckOnce()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            EditorApplication.update -= CheckOnce;
            if (SessionState.GetBool(PromptKey, false)) return;
            SessionState.SetBool(PromptKey, true);
            Debug.LogWarning("[MCP for Unity Launcher] Install a compatible MCP for Unity package. The setup window provides VPM and UPM instructions; reopen it from Window > MCP for Unity Launcher.");
            LauncherDependencyWindow.Open();
        }
#endif
    }

    internal sealed class LauncherDependencyWindow : EditorWindow
    {
        internal const string VpmPageUrl = "https://vrclearn.github.io/MCP-For-Unity-Launcher/";
        internal const string VpmRepositoryUrl = VpmPageUrl + "index.json";
        internal const string DependencyGitUrl = "https://github.com/VRCLearn/unity-mcp.git?path=/MCPForUnity#10.3.0";

        private Vector2 _scroll;

#if !MCP_FOR_UNITY_LAUNCHER_HAS_MCP
        [MenuItem("Window/MCP for Unity Launcher")]
#endif
        internal static void Open()
        {
            var window = GetWindow<LauncherDependencyWindow>(Text("MCP Launcher Setup"));
            window.minSize = new Vector2(520, 400);
            window.Show();
        }

        private void OnEnable() => LanguageChanged += RefreshLanguage;
        private void OnDisable() => LanguageChanged -= RefreshLanguage;
        private void OnInspectorUpdate() => RefreshLanguage();

        private void RefreshLanguage()
        {
            titleContent = new GUIContent(Text("MCP Launcher Setup"));
            Repaint();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("MCP for Unity Launcher", EditorStyles.boldLabel);
            DrawLanguageSelector();
            EditorGUILayout.Space();
#if MCP_FOR_UNITY_LAUNCHER_HAS_MCP
            EditorGUILayout.HelpBox(Text("MCP for Unity is installed. Launcher will automatically start and connect to the local server."), MessageType.Info);
            if (GUILayout.Button(Text("Open Launcher")))
            {
                EditorApplication.ExecuteMenuItem("Window/MCP for Unity Launcher");
                Close();
            }
#else
            EditorGUILayout.HelpBox(Text("Launcher requires MCP for Unity 10.3.0 or a newer 10.x version. Install it in this project; Launcher enables automatically after installation and recompilation."), MessageType.Warning);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Text("Install through VPM"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(Text("For VCC / ALCOMD: add the VPM repository below, then install MCP for Unity in this project's package manager."), MessageType.Info);
            EditorGUILayout.SelectableLabel(VpmRepositoryUrl, GUILayout.Height(36));
            if (GUILayout.Button(Text("Open VPM installation page"))) Application.OpenURL(VpmPageUrl);
            if (GUILayout.Button(Text("Copy VPM repository URL"))) EditorGUIUtility.systemCopyBuffer = VpmRepositoryUrl;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Text("Install through UPM"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(Text("For any Unity project, without VRChat or VCC: open Window > Package Manager, choose + > Add package from git URL, then paste the URL below and install."), MessageType.Info);
            EditorGUILayout.SelectableLabel(DependencyGitUrl, GUILayout.Height(54));
            if (GUILayout.Button(Text("Copy MCP for Unity UPM URL"))) EditorGUIUtility.systemCopyBuffer = DependencyGitUrl;
            if (GUILayout.Button(Text("Open Unity Package Manager"))) EditorApplication.ExecuteMenuItem("Window/Package Manager");

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(Text("Use one installation method per project. Automatic server startup also requires uv / uvx to be installed."), MessageType.Info);
#endif
            EditorGUILayout.EndScrollView();
        }
    }
}

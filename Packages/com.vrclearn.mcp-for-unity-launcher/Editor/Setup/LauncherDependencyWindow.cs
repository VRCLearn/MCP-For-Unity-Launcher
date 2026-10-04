using UnityEditor;
using UnityEngine;

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
            Debug.LogWarning("[MCP for Unity Launcher] 请先安装兼容的 MCP for Unity。安装窗口提供 VPM 和 UPM 两种方式，也可从 Window → MCP for Unity Launcher 重新打开。");
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
            var window = GetWindow<LauncherDependencyWindow>("MCP Launcher Setup");
            window.minSize = new Vector2(520, 400);
            window.Show();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("MCP for Unity Launcher", EditorStyles.boldLabel);
            EditorGUILayout.Space();
#if MCP_FOR_UNITY_LAUNCHER_HAS_MCP
            EditorGUILayout.HelpBox("MCP for Unity 已安装。Launcher 会自动启动并连接本地服务。", MessageType.Info);
            if (GUILayout.Button("打开 Launcher"))
            {
                EditorApplication.ExecuteMenuItem("Window/MCP for Unity Launcher");
                Close();
            }
#else
            EditorGUILayout.HelpBox("Launcher 需要 MCP for Unity 10.3.0 或更新的 10.x 版本。请在当前项目中安装依赖；安装完成并重新编译后，Launcher 会自动启用。", MessageType.Warning);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("通过 VPM 安装", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("适用于 VCC / ALCOMD：添加下面的 VPM 仓库，然后在当前项目的包管理页面安装 MCP for Unity。", MessageType.Info);
            EditorGUILayout.SelectableLabel(VpmRepositoryUrl, GUILayout.Height(36));
            if (GUILayout.Button("打开 VPM 安装页面")) Application.OpenURL(VpmPageUrl);
            if (GUILayout.Button("复制 VPM 仓库地址")) EditorGUIUtility.systemCopyBuffer = VpmRepositoryUrl;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("通过 UPM 安装", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("适用于任何 Unity 项目，无需 VRChat 或 VCC：打开 Window → Package Manager，选择 + → Add package from git URL，粘贴下面的地址并安装。", MessageType.Info);
            EditorGUILayout.SelectableLabel(DependencyGitUrl, GUILayout.Height(54));
            if (GUILayout.Button("复制 MCP for Unity 的 UPM 地址")) EditorGUIUtility.systemCopyBuffer = DependencyGitUrl;
            if (GUILayout.Button("打开 Unity Package Manager")) EditorApplication.ExecuteMenuItem("Window/Package Manager");

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("同一项目请只使用一种方式安装 MCP for Unity。Launcher 的服务自动启动功能还需要已安装的 uv / uvx。", MessageType.Info);
#endif
            EditorGUILayout.EndScrollView();
        }
    }
}

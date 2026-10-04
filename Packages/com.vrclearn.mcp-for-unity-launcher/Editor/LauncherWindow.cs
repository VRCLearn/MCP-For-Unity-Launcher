using System.IO;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using UnityEditor;
using UnityEngine;

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

        private void OnInspectorUpdate() => Repaint();

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("MCP for Unity Launcher", EditorStyles.boldLabel);
            EditorGUILayout.Space();
            bool enabled = EditorGUILayout.ToggleLeft("自动管理当前项目的 MCP 服务", LauncherBootstrap.Enabled);
            if (enabled != LauncherBootstrap.Enabled) LauncherBootstrap.Enabled = enabled;
            EditorGUILayout.HelpBox("打开项目后自动启动和连接。关闭编辑器 A 后，只要编辑器 B 仍在使用 Launcher，共享服务会继续运行；意外中断会自动恢复。", MessageType.Info);

            if (LauncherBootstrap.IsRemote)
                EditorGUILayout.HelpBox("当前使用 HTTP Remote，Launcher 保留远程设置，不启动本地服务。", MessageType.Info);
            else
                EditorGUILayout.SelectableLabel("MCP 地址：" + LauncherBootstrap.BaseUrl.TrimEnd('/') + "/mcp", GUILayout.Height(20));

            var client = MCPServiceLocator.TransportManager.GetClient(TransportMode.Http);
            EditorGUILayout.LabelField("当前项目连接", client != null && client.IsConnected ? "已连接" :
                LauncherBootstrap.CanManageLocalServer ? "未连接 / 等待自动连接" : "未连接");
            if (!string.IsNullOrEmpty(client?.State?.SessionId) && client.State.SessionId != "pending")
                EditorGUILayout.LabelField("会话 ID", client.State.SessionId);

            if (!string.IsNullOrEmpty(LauncherBootstrap.LastError))
                EditorGUILayout.HelpBox(LauncherBootstrap.LastError, MessageType.Warning);

            var status = LauncherBootstrap.ReadStatus();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("共享守护进程", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(LauncherBootstrap.IsSupervisorHealthy ? "运行中" : "尚未运行或等待恢复");
            if (status != null)
            {
                EditorGUILayout.LabelField("PID", status.supervisorPid.ToString());
                EditorGUILayout.LabelField("最近更新", status.updatedUtc ?? "");
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("已注册编辑器", EditorStyles.boldLabel);
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
                EditorGUILayout.LabelField("服务", EditorStyles.boldLabel);
                if (status.services != null)
                {
                    foreach (var service in status.services)
                    {
                        if (service == null) continue;
                        EditorGUILayout.SelectableLabel(service.baseUrl ?? "", GUILayout.Height(18));
                        EditorGUILayout.LabelField("状态", TranslateState(service.state));
                        EditorGUILayout.LabelField("管理方式", service.owned ? "Launcher 管理" : "连接已有服务");
                        if (service.pid > 0) EditorGUILayout.LabelField("PID", service.pid.ToString());
                        if (service.restartCount > 0) EditorGUILayout.LabelField("恢复次数", service.restartCount.ToString());
                        if (!string.IsNullOrEmpty(service.error)) EditorGUILayout.HelpBox(service.error, MessageType.Warning);
                    }
                }
            }
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("日志目录", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(LauncherBootstrap.LogDirectory, GUILayout.Height(36));
            if (GUILayout.Button("打开日志目录"))
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
                case "healthy": return "运行中";
                case "starting": return "正在启动";
                case "external": return "已有服务";
                case "retrying":
                case "backoff": return "等待自动恢复";
                case "unhealthy": return "服务异常，正在检测";
                case "blocked": return "端口被其他程序占用";
                case "conflict": return "项目的服务配置不一致";
                case "waiting": return "等待启动";
                case "idle": return "等待最后一个编辑器退出";
                case "error": return "发生错误";
                case "stopped": return "已停止";
                default: return state ?? "等待状态";
            }
        }
    }
}

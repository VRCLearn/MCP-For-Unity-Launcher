using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

public static class LauncherCompilationProbe
{
    private static bool _nonEnglishDiagnostics;

    public static async void Run()
    {
        _nonEnglishDiagnostics = false;
        Application.logMessageReceived += CaptureDiagnostics;
        try
        {
            var expected = Environment.GetEnvironmentVariable("MCP_LAUNCHER_EXPECT_INTEGRATION");
            Require(expected == "0" || expected == "1", "Explicit expected integration state is required");
            bool integration = expected == "1";
            Require(!EditorUtility.scriptCompilationFailed, "Script compilation failed");
            var assemblies = CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                .Where(a => a.name.StartsWith("MCPForUnityLauncher")).Select(a => a.name).ToArray();
            Require(assemblies.Contains("MCPForUnityLauncher.Setup.Editor"), "Setup assembly is missing");
            Require(assemblies.Contains("MCPForUnityLauncher.Editor") == integration, "Unexpected integration state");
            foreach (var assembly in assemblies) Debug.Log("LAUNCHER_ASSEMBLY: " + assembly);

            var menus = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name.StartsWith("MCPForUnityLauncher"))
                .SelectMany(a => a.GetTypes()).SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                .SelectMany(m => m.GetCustomAttributes(typeof(MenuItem), false).Cast<MenuItem>())
                .Count(m => m.menuItem == "Window/MCP for Unity Launcher");
            Require(menus == 1, "Expected exactly one Launcher menu");
            Require(LauncherWindows().Length == 0, "Batch import automatically opened a Launcher window");
            Require(EditorApplication.ExecuteMenuItem("Window/MCP for Unity Launcher"), "Menu failed");
            var target = integration ? "LauncherWindow" : "LauncherDependencyWindow";
            var windows = LauncherWindows();
            Require(windows.Length == 1 && windows[0].GetType().Name == target, "Unexpected menu window");
            Debug.Log("LAUNCHER_MENU_WINDOW: " + target);
            foreach (var window in windows) window.Close();

            if (!integration)
            {
                var prompt = Type.GetType("MCPForUnityLauncher.Editor.LauncherDependencyPrompt, MCPForUnityLauncher.Setup.Editor", true);
                var check = prompt.GetMethod("CheckOnce", BindingFlags.Static | BindingFlags.NonPublic);
                SessionState.SetBool("MCPForUnityLauncher.DependencyPromptShown", false);
                check.Invoke(null, null);
                windows = LauncherWindows();
                Require(windows.Length == 1 && windows[0].GetType().Name == target, "First prompt did not open");
                foreach (var window in windows) window.Close();
                check.Invoke(null, null);
                Require(LauncherWindows().Length == 0, "Second prompt reopened");
                Debug.Log("LAUNCHER_PROMPT_DEDUP: passed");
            }

            if (integration)
                await (Task)Type.GetType("LauncherConnectionProbe, Assembly-CSharp-Editor", true).GetMethod("Run").Invoke(null, null);
            LauncherLocalizationProbe.Run();
            Require(!_nonEnglishDiagnostics, "Launcher emitted a non-English diagnostic");
            Debug.Log("LAUNCHER_VERIFICATION: passed");
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(9);
        }
        finally
        {
            Application.logMessageReceived -= CaptureDiagnostics;
        }
    }

    private static void CaptureDiagnostics(string message, string stackTrace, LogType type)
    {
        if (message.StartsWith("[MCP for Unity Launcher]"))
            _nonEnglishDiagnostics |= message.Any(c => c > 127);
    }

    private static EditorWindow[] LauncherWindows()
    {
        return Resources.FindObjectsOfTypeAll<EditorWindow>()
            .Where(w => w.GetType().Name == "LauncherWindow" || w.GetType().Name == "LauncherDependencyWindow").ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}

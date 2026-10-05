using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MCPForUnityLauncher.Editor;

// The production helper is compiled unchanged. This stub selects its actual
// native branch without requiring a licensed Unity Editor on every CI runner.
namespace UnityEngine
{
    internal enum RuntimePlatform { WindowsEditor, LinuxEditor, OSXEditor }
    internal static class Application
    {
        public static RuntimePlatform platform => RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? RuntimePlatform.OSXEditor : RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? RuntimePlatform.WindowsEditor : RuntimePlatform.LinuxEditor;
    }
}

internal static class Program
{
    private static void Main()
    {
        using (var process = Process.GetCurrentProcess())
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                pid = process.Id,
                start = ProcessIdentity.StartFileTimeUtc(process).ToString()
            }));
            Console.Out.Flush();
            Console.ReadLine();
        }
    }
}

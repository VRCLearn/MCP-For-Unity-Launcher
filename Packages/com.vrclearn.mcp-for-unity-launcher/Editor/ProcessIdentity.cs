using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MCPForUnityLauncher.Editor
{
    internal static class ProcessIdentity
    {
        // Darwin proc_bsdinfo from <sys/proc_info.h>. Only the required fields
        // are exposed; the full buffer size and native offsets remain intact.
        [StructLayout(LayoutKind.Explicit, Size = 136)]
        private struct MacProcessInfo
        {
            [FieldOffset(120)] public ulong StartSeconds;
            [FieldOffset(128)] public ulong StartMicroseconds;
        }

        [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_pidinfo", SetLastError = true)]
        private static extern int QueryMacProcess(int pid, int flavor, ulong argument,
            out MacProcessInfo info, int size);

        [DllImport("libc", EntryPoint = "sysconf", SetLastError = true)]
        private static extern IntPtr LinuxSystemConfiguration(int name);

        private static long LinuxStartFileTimeUtc(int pid)
        {
            string stat = File.ReadAllText("/proc/" + pid + "/stat");
            string[] fields = stat.Substring(stat.LastIndexOf(')') + 2)
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            long ticks = long.Parse(fields[19], CultureInfo.InvariantCulture);
            // Linux _SC_CLK_TCK is 2; libc returns a native long.
            long frequency = LinuxSystemConfiguration(2).ToInt64();
            if (frequency <= 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not query Linux clock frequency");
            foreach (string line in File.ReadLines("/proc/stat"))
            {
                if (!line.StartsWith("btime ", StringComparison.Ordinal))
                    continue;
                long boot = long.Parse(line.Substring(6).Trim(), CultureInfo.InvariantCulture);
                return checked(116444736000000000L + boot * 10000000L + ticks * 10000000L / frequency);
            }
            throw new IOException("Could not query Linux boot time");
        }

        internal static long StartFileTimeUtc(Process process)
        {
            if (Application.platform == RuntimePlatform.LinuxEditor)
                return LinuxStartFileTimeUtc(process.Id);
            if (Application.platform != RuntimePlatform.OSXEditor)
                return process.StartTime.ToUniversalTime().ToFileTimeUtc();

            int size = Marshal.SizeOf(typeof(MacProcessInfo));
            if (QueryMacProcess(process.Id, 3, 0, out var info, size) != size)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not query macOS process identity");
            return checked(116444736000000000L + (long)info.StartSeconds * 10000000L +
                (long)info.StartMicroseconds * 10L);
        }
    }
}

using System;
using System.ComponentModel;
using System.Diagnostics;
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

        internal static long StartFileTimeUtc(Process process)
        {
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

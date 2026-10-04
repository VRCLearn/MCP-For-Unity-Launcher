using MCPForUnity.Editor.Services;

namespace MCPForUnityLauncher.Editor
{
    internal sealed class LauncherServerService : IServerManagementService
    {
        internal readonly IServerManagementService Original;

        internal LauncherServerService(IServerManagementService original)
        {
            Original = original;
        }

        public bool ClearUvxCache() => Original.ClearUvxCache();
        public bool StartLocalHttpServer(bool quiet = false)
        {
            return LauncherBootstrap.RequestServerStart();
        }

        public bool StopLocalHttpServer()
        {
            LauncherBootstrap.ReportSharedStop();
            return false;
        }

        // Unity's upstream quit handler reads a machine-wide handshake. Our own
        // quit hook releases only this editor's lease; the supervisor owns shutdown.
        public bool StopManagedLocalHttpServer() => false;
        public bool IsLocalHttpServerRunning() => Original.IsLocalHttpServerRunning();
        public bool IsLocalHttpServerReachable() => Original.IsLocalHttpServerReachable();
        public bool TryGetLocalHttpServerCommand(out string command, out string error)
            => Original.TryGetLocalHttpServerCommand(out command, out error);
        public bool IsLocalUrl() => Original.IsLocalUrl();
        public bool CanStartLocalServer() => LauncherBootstrap.CanManageLocalServer;
        public string GetLocalHttpServerLaunchLogPath() => LauncherBootstrap.LaunchLogPath;
        public bool IsManagedServerLaunchProcessAlive() => LauncherBootstrap.IsSupervisorLaunchAlive;
        public bool HasManagedServerLaunchHandle => LauncherBootstrap.HasSupervisorLaunch;
        public void LogLocalHttpServerLaunchFailure() => LauncherBootstrap.ReportLaunchFailure();
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;

// External API shapes only. The production lifecycle implementation is linked
// by the project file; none of its connection or timeout logic is duplicated.
namespace MCPForUnity.Editor.Services.Transport
{
    public interface IMcpTransportClient
    {
        string TransportName { get; }
        bool IsConnected { get; }
        TransportState State { get; }
        Task<bool> StartAsync();
        Task StopAsync();
        Task<bool> VerifyAsync();
        Task ReregisterToolsAsync();
    }

    public sealed class TransportState
    {
        public bool IsConnected { get; private set; }
        public string SessionId { get; private set; }
        public string Error { get; private set; }
        public static TransportState Connected(string transportName, int? port = null,
            string sessionId = null, string details = null)
            => new TransportState { IsConnected = true, SessionId = sessionId };
        public static TransportState Disconnected(string transportName, string error = null, int? port = null)
            => new TransportState { Error = error };
    }
}

namespace MCPForUnity.Editor.Services
{
    public static class MCPServiceLocator
    {
        public static object ToolDiscovery => throw new NotSupportedException("The CI probe must inject its transport factory.");
    }
}

namespace MCPForUnity.Editor.Services.Transport.Transports
{
    using MCPForUnity.Editor.Services.Transport;
    // Exists solely for the production type check in Retire. Tests use an
    // instance to assert the synchronous WebSocket ForceStop path is selected.
    public sealed class WebSocketTransportClient : IMcpTransportClient
    {
        public WebSocketTransportClient(object discovery = null) { }
        public int ForcedStops;
        public int GracefulStops;
        public string TransportName => "stub-websocket";
        public bool IsConnected { get; private set; }
        public TransportState State => IsConnected ? TransportState.Connected(TransportName, sessionId: "raw") : TransportState.Disconnected(TransportName);
        public Task<bool> StartAsync() { IsConnected = true; return Task.FromResult(true); }
        public Task StopAsync() { GracefulStops++; throw new InvalidOperationException("Graceful socket stop must not be used by retirement."); }
        public void ForceStop() { ForcedStops++; IsConnected = false; }
        public Task<bool> VerifyAsync() => Task.FromResult(IsConnected);
        public Task ReregisterToolsAsync() => Task.CompletedTask;
    }
}

namespace MCPForUnityLauncher.Editor
{
    internal static class LauncherBootstrap
    {
        internal static bool CanManageLocalServer => throw new NotSupportedException("The CI probe must inject its management predicate.");
    }
    internal static class ProjectRegistrationProbe
    {
        internal static Task<bool> WaitForServerAsync(CancellationToken token)
            => throw new NotSupportedException("The CI probe must inject its server readiness wait.");
        internal static Task<string> VerifyAsync(CancellationToken token)
            => throw new NotSupportedException("The CI probe does not perform real Unity registration or network I/O.");
    }
}

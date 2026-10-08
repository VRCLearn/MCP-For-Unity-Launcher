using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Services.Transport.Transports;

namespace MCPForUnityLauncher.Editor
{
    // TransportManager coalesces StartAsync calls. Every operation must finish even
    // when an upstream socket task does not; retired clients are never reused.
    internal sealed class ManagedHttpTransportClient : IMcpTransportClient
    {
        private readonly Func<IMcpTransportClient> _factory;
        private readonly Func<CancellationToken, Task<string>> _probe;
        private readonly Func<bool> _managed;
        private readonly TimeSpan _timeout;
        private readonly Func<CancellationToken, Task<bool>> _waitForServer;
        private IMcpTransportClient _client;
        private CancellationTokenSource _attempt;
        private int _generation;
        private bool _local;
        private string _session;
        private string _confirmedRawSession;
        private string _error;
        internal bool WaitingForServer { get; private set; }
        internal string LastStartError { get; private set; }

        internal ManagedHttpTransportClient() : this(
            () => new WebSocketTransportClient(MCPServiceLocator.ToolDiscovery),
            ProjectRegistrationProbe.VerifyAsync, () => LauncherBootstrap.CanManageLocalServer,
            TimeSpan.FromSeconds(45), ProjectRegistrationProbe.WaitForServerAsync) { }

        internal ManagedHttpTransportClient(Func<IMcpTransportClient> factory,
            Func<CancellationToken, Task<string>> probe, Func<bool> managed, TimeSpan timeout)
            : this(factory, probe, managed, timeout, null) { }

        internal ManagedHttpTransportClient(Func<IMcpTransportClient> factory,
            Func<CancellationToken, Task<string>> probe, Func<bool> managed, TimeSpan timeout,
            Func<CancellationToken, Task<bool>> waitForServer)
        {
            _factory = factory;
            _probe = probe;
            _managed = managed;
            _timeout = timeout;
            _waitForServer = waitForServer ?? (token => Task.FromResult(true));
        }

        public string TransportName => "http";
        public bool IsConnected
        {
            get
            {
                if (_client == null) return false;
                if (!_local || !_managed()) return _client.IsConnected;
                if (!_client.IsConnected || (_session != null && _client.State?.SessionId != _confirmedRawSession))
                {
                    _session = null;
                    _error = _client.State?.Error ?? "Project session changed; registration must be verified again.";
                }
                return _client.IsConnected && _session != null;
            }
        }
        public TransportState State => IsConnected
            ? (_local && _managed() ? TransportState.Connected(TransportName, sessionId: _session) : _client.State)
            : TransportState.Disconnected(TransportName, _error ?? _client?.State?.Error);

        public async Task<bool> StartAsync()
        {
            await StopAsync();
            int generation = _generation;
            var cancellation = new CancellationTokenSource();
            _attempt = cancellation;
            _local = _managed();
            bool local = _local;
            LastStartError = null;
            IMcpTransportClient client = null;
            Task<bool> start = null;
            string session = null;
            var elapsed = Stopwatch.StartNew();
            try
            {
                if (local)
                {
                    WaitingForServer = true;
                    bool ready = await WithinAsync(_waitForServer(cancellation.Token), _timeout, cancellation.Token);
                    if (generation != _generation) return false;
                    if (!ready) throw new InvalidOperationException("The local MCP server is not ready.");
                    WaitingForServer = false;
                }
                cancellation.Token.ThrowIfCancellationRequested();
                if (local && !_managed()) throw new OperationCanceledException("Local management changed during connection.");
                var remaining = _timeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("Project connection deadline expired.");
                client = _factory();
                _client = client;
                start = client.StartAsync();
                bool connected = await WithinAsync(start, remaining, cancellation.Token);
                if (!connected || generation != _generation) return false;
                if (local)
                {
                    // Probe retries registration races, but the entire attempt has a
                    // deadline independent of the upstream handshake implementation.
                    remaining = _timeout - elapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero) throw new TimeoutException("Project connection deadline expired.");
                    session = await WithinAsync(_probe(cancellation.Token), remaining, cancellation.Token);
                    if (string.IsNullOrEmpty(session)) throw new InvalidOperationException("Project registration is incomplete.");
                }
                if (generation != _generation) return false;
                _session = session;
                _confirmedRawSession = client.State?.SessionId;
                _error = null;
                return IsConnected;
            }
            catch (Exception exception)
            {
                if (generation == _generation)
                {
                    _session = null;
                    _error = exception is OperationCanceledException ? "Project connection was cancelled." : exception.Message;
                    if (exception is TimeoutException && WaitingForServer)
                        _error = "The local MCP server did not become ready before the connection deadline. Retrying automatically.";
                    LastStartError = _error;
                    cancellation.Cancel();
                    Retire(client);
                }
                return false;
            }
            finally
            {
                if (start != null && (!start.IsCompleted || generation != _generation))
                    _ = RetireAfterAsync(start, client);
                if (ReferenceEquals(_attempt, cancellation)) _attempt = null;
                if (generation == _generation) WaitingForServer = false;
                cancellation.Dispose();
            }
        }

        public Task StopAsync()
        {
            _generation++;
            _attempt?.Cancel();
            var client = _client;
            _client = null;
            _session = null;
            _error = null;
            WaitingForServer = false;
            Retire(client);
            return Task.CompletedTask;
        }

        internal void CancelPendingLocalStart()
        {
            if (_local && _attempt != null) _ = StopAsync();
        }

        public async Task<bool> VerifyAsync()
        {
            var client = _client;
            int generation = _generation;
            if (client == null || !client.IsConnected) return false;
            bool local = _local && _managed();
            using (var cancellation = new CancellationTokenSource())
            {
                try
                {
                    var timeout = _timeout < TimeSpan.FromSeconds(10) ? _timeout : TimeSpan.FromSeconds(10);
                    if (local)
                    {
                        string session = await WithinAsync(_probe(cancellation.Token), timeout, cancellation.Token);
                        if (generation != _generation || !_managed()) return false;
                        if (string.IsNullOrEmpty(session)) throw new InvalidOperationException("Project registration is incomplete.");
                        _session = session;
                        _confirmedRawSession = client.State?.SessionId;
                    }
                    else if (!await WithinAsync(client.VerifyAsync(), timeout, cancellation.Token)) return false;
                    if (generation != _generation) return false;
                    _error = null;
                    return IsConnected;
                }
                catch (Exception exception)
                {
                    if (generation == _generation && local && _managed())
                    {
                        _session = null;
                        _error = exception.Message;
                    }
                    return false;
                }
                finally { cancellation.Cancel(); }
            }
        }

        public async Task ReregisterToolsAsync()
        {
            var client = _client;
            if (client != null)
                await WithinAsync(AsResultAsync(client.ReregisterToolsAsync()), _timeout, CancellationToken.None);
        }

        private static async Task<bool> AsResultAsync(Task task) { await task; return true; }

        private static async Task<T> WithinAsync<T>(Task<T> task, TimeSpan timeout, CancellationToken token)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var delay = Task.Delay(timeout, deadline.Token);
                if (await Task.WhenAny(task, delay) != task)
                {
                    _ = ObserveAsync(task);
                    token.ThrowIfCancellationRequested();
                    throw new TimeoutException("Timed out waiting for the Unity project connection or registration.");
                }
                deadline.Cancel();
                return await task;
            }
        }

        private static void Retire(IMcpTransportClient client)
        {
            try
            {
                if (client is WebSocketTransportClient socket) socket.ForceStop();
                else if (client != null) _ = ObserveAsync(client.StopAsync());
            }
            catch { /* A failed old cleanup must not block a fresh connection. */ }
        }

        private static async Task RetireAfterAsync(Task task, IMcpTransportClient client)
        {
            await ObserveAsync(task);
            Retire(client);
        }

        private static async Task ObserveAsync(Task task)
        {
            try { await task; }
            catch { /* Observe retired operations without changing the active state. */ }
        }
    }
}

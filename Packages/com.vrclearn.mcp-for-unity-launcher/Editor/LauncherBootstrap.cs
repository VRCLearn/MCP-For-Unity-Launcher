using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Services.Transport.Transports;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MCPForUnityLauncher.Editor
{
    // Unity's JsonUtility populates these fields when reading supervisor status.
#pragma warning disable CS0649
    [Serializable]
    internal sealed class EditorLease
    {
        public int schemaVersion = 1;
        public int processId;
        public string processStartFileTimeUtc;
        public string projectPath;
        public string baseUrl;
        public string executable;
        public string[] arguments;
        public string packageVersion;
        public bool released;
    }

    [Serializable]
    internal sealed class SupervisorStatus
    {
        public int schemaVersion;
        public int supervisorPid;
        public string updatedUtc;
        public EditorLease[] editors;
        public ServiceStatus[] services;
    }

    [Serializable]
    internal sealed class ServiceStatus
    {
        public string baseUrl;
        public string state;
        public bool owned;
        public int pid;
        public string error;
        public int restartCount;
        public string nextRetryUtc;
    }
#pragma warning restore CS0649

    [InitializeOnLoad]
    internal static class LauncherBootstrap
    {
        private const string LogPrefix = "[MCP for Unity Launcher] ";
        private static readonly string ProjectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        private static readonly string PreferenceKey = "MCPForUnityLauncher.Enabled." + Hash(ProjectPath);
        internal static readonly string StateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCP-For-Unity-Launcher");
        private static readonly CancellationTokenSource Lifetime = new CancellationTokenSource();
        private static LauncherServerService _wrapper;
        private static string _leasePath;
        private static string _scriptPath;
        private static string _packageVersion;
        private static string _lastLeaseJson;
        private static string _lastReleaseJson;
        private static string _lastError;
        private static string _lastConnectionError;
        private static int _processId;
        private static string _processStart;
        private static double _nextTick;
        private static DateTime _nextSpawnUtc;
        private static double _nextConnect;
        private static int _connectFailures;
        private static bool _connectInFlight;
        private static bool _connectedOnce;
        private static bool _needsNewEndpoint;
        private static string _lastLocalUrl;
        private static TransportManager _configuredManager;
        private static double _disconnectedSince = -1;
        private static double _nextVerify; // Retry deadline for an unconfirmed recovered socket only.
        private static bool _verificationInFlight;
        private static int _connectionGeneration;
        private static string _lastVerifiedUtc;
        private static bool _quitting;
        private static Process _launchProcess;

        internal static string LastError => _lastError ?? _lastConnectionError;
        internal static string LastVerifiedUtc => _lastVerifiedUtc;
        internal static int RetrySeconds => (int)Math.Max(0, Math.Ceiling(_nextConnect - EditorApplication.timeSinceStartup));
        internal static string ConnectionPhase =>
            (MCPServiceLocator.TransportManager.GetClient(TransportMode.Http) as ManagedHttpTransportClient)?.WaitingForServer == true
                ? "Waiting for local server startup" : _connectInFlight ? "Connecting and registering" :
            _verificationInFlight ? "Verifying project connection" :
            _disconnectedSince != -1 ? "Waiting for upstream reconnection" :
            RetrySeconds > 0 ? "Waiting for automatic recovery" : "Disconnected / waiting for automatic connection";
        internal static string LogDirectory => StateDirectory;
        internal static string LaunchLogPath => Path.Combine(StateDirectory, "supervisor.log");
        internal static bool HasSupervisorLaunch => _launchProcess != null;
        internal static bool IsSupervisorLaunchAlive => _launchAlive || IsSupervisorHealthy;
        internal static bool Enabled
        {
            get => EditorPrefs.GetBool(PreferenceKey, true);
            set
            {
                if (Enabled == value) return;
                EditorPrefs.SetBool(PreferenceKey, value);
                CancelMaintenance();
                if (value)
                {
                    InstallWrapper();
                    InstallTransport();
                    _connectedOnce = false;
                    _nextTick = 0;
                    _nextConnect = 0;
                }
                else
                {
                    _connectionGeneration++;
                    CancelPendingConnection();
                    RemoveLease();
                    RestoreWrapper();
                    _connectedOnce = false;
                    _disconnectedSince = -1;
                }
            }
        }

        internal static bool IsRemote => HttpEndpointUtility.IsRemoteScope();
        internal static string BaseUrl => HttpEndpointUtility.GetLocalBaseUrl();
        internal static bool CanManageLocalServer => Enabled && !IsRemote && IsAllowedUrl(BaseUrl);
        private static Task<MaintenanceResult> _maintenance;
        private static CancellationTokenSource _maintenanceCancellation;
        private static int _maintenanceGeneration;
        private static ServerSettings _settings;
        private static ServerSettings _activeSettings;
        private static bool _releaseQueued;
        private static SupervisorStatus _status;
        private static bool _supervisorHealthy;
        private static bool _launchAlive;
        private static string _packageRoot;

        private sealed class ServerSettings
        {
            internal string Key;
            internal string Root;
            internal string Version;
            internal string UvxOverride;
            internal string Url;
            internal string[] Arguments;
            internal string Executable;
        }

        private sealed class MaintenanceResult
        {
            internal int Generation;
            internal SupervisorStatus Status;
            internal bool Healthy;
            internal bool Started;
            internal bool LaunchAlive;
            internal string Error;
            internal bool Released;
        }

        internal static bool IsSupervisorHealthy => _supervisorHealthy && StatusIsFresh(_status);

        private static bool StatusIsFresh(SupervisorStatus status)
        {
            if (status == null || status.supervisorPid <= 0 || !DateTimeOffset.TryParse(
                    status.updatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var updated))
                return false;
            var age = DateTimeOffset.UtcNow - updated;
            return age.TotalSeconds >= -5 && age.TotalSeconds <= 15;
        }

        static LauncherBootstrap()
        {
            // Protect upstream startup and quitting before deferred editor work runs.
            if (!Application.isBatchMode && Enabled)
            {
                InstallWrapper();
                InstallTransport();
            }
            if (Application.isBatchMode) return;
            EditorApplication.update += Update;
            EditorApplication.quitting += OnQuitting;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
        }

        private static void InstallWrapper()
        {
            var current = MCPServiceLocator.Server;
            if (ReferenceEquals(current, _wrapper)) return;
            _wrapper = new LauncherServerService(current);
            MCPServiceLocator.Register<IServerManagementService>(_wrapper);
        }

        private static void RestoreWrapper()
        {
            if (_wrapper != null && ReferenceEquals(MCPServiceLocator.Server, _wrapper))
                MCPServiceLocator.Register<IServerManagementService>(_wrapper.Original);
            _wrapper = null;
        }

        private static void InstallTransport()
        {
            var manager = MCPServiceLocator.TransportManager;
            if (ReferenceEquals(manager, _configuredManager) || manager.GetClient(TransportMode.Http) != null) return;
            manager.Configure(() => new ManagedHttpTransportClient(), () => new StdioTransportClient());
            _configuredManager = manager;
        }

        private static void Update()
        {
            CompleteMaintenance();
            if (_quitting || Lifetime.IsCancellationRequested || EditorApplication.timeSinceStartup < _nextTick) return;
            _nextTick = EditorApplication.timeSinceStartup + 2;
            if (!Enabled)
            {
                RemoveLease();
                RestoreWrapper();
                return;
            }
            try
            {
                InstallWrapper();
                InstallTransport();
                if (IsRemote)
                {
                    if (_connectInFlight)
                        _connectionGeneration++;
                    CancelPendingConnection();
                    RemoveLease();
                    _connectedOnce = false;
                    _disconnectedSince = -1;
                    _needsNewEndpoint = true;
                    return;
                }
                if (!IsAllowedUrl(BaseUrl))
                    throw new InvalidOperationException("The local MCP URL must be a loopback HTTP address, such as http://127.0.0.1:8080.");
                EditorConfigurationCache.Instance.SetUseHttpTransport(true);
                if (_lastLocalUrl != null && _lastLocalUrl != BaseUrl)
                {
                    _connectionGeneration++;
                    CancelPendingConnection();
                    CancelMaintenance();
                    _connectedOnce = false;
                    _disconnectedSince = -1;
                    _needsNewEndpoint = true;
                    _nextConnect = 0;
                }
                _lastLocalUrl = BaseUrl;
                // A running server can accept this project even if local launch fails.
                QueueMaintenance();
                TryConnect();
            }
            catch (Exception exception) { SetError(exception.Message); }
        }

        internal static bool RequestServerStart()
        {
            if (!CanManageLocalServer) return false;
            _nextSpawnUtc = DateTime.MinValue;
            QueueMaintenance();
            return true;
        }

        // Capture Unity configuration on the Editor thread. No executable is run here.
        private static ServerSettings CaptureSettings()
        {
            string uvx = EditorPrefs.GetString("MCPForUnity.UvxPath", "");
            string source = EditorPrefs.GetString("MCPForUnity.GitUrlOverride", "");
            bool refresh = EditorPrefs.GetBool("MCPForUnity.DevModeForceServerRefresh", false);
            bool scoped = EditorPrefs.GetBool("MCPForUnity.ProjectScopedTools.LocalHttp", true);
            string url = BaseUrl;
            string key = JsonUtility.ToJson(new EditorLease { executable = uvx, baseUrl = url,
                arguments = new[] { source, refresh.ToString(), scoped.ToString() } });
            if (_settings != null && _settings.Key == key) return _settings;
            if (_packageRoot == null)
            {
                var package = PackageInfo.FindForAssembly(typeof(LauncherBootstrap).Assembly);
                _packageRoot = package?.resolvedPath;
                _packageVersion = package?.version;
                if (string.IsNullOrEmpty(_packageRoot))
                {
                    foreach (var guid in AssetDatabase.FindAssets("LauncherBootstrap t:MonoScript"))
                    {
                        string asset = AssetDatabase.GUIDToAssetPath(guid);
                        if (Path.GetFileName(asset) != "LauncherBootstrap.cs") continue;
                        _packageRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(asset), ".."));
                        break;
                    }
                    if (_packageRoot != null)
                        _packageVersion = JsonUtility.FromJson<PackageMetadata>(File.ReadAllText(
                            Path.Combine(_packageRoot, "package.json"))).version;
                }
                if (string.IsNullOrEmpty(_packageRoot) || string.IsNullOrEmpty(_packageVersion))
                    throw new InvalidOperationException("Could not locate the Launcher package and version.");
            }
            var arguments = new List<string>();
            // Preserve explicit development refresh flags, without invoking the upstream offline probe.
            string flags = AssetPathUtility.GetUvxDevFlags(AssetPathUtility.ShouldForceUvxRefresh(), false);
            arguments.AddRange(flags.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            arguments.AddRange(AssetPathUtility.GetBetaServerFromArgsList());
            arguments.AddRange(new[] { "mcp-for-unity", "--transport", "http", "--http-url", url });
            if (scoped) arguments.Add("--project-scoped-tools");
            _settings = new ServerSettings { Key = key, Root = _packageRoot, Version = _packageVersion,
                UvxOverride = uvx, Url = url, Arguments = arguments.ToArray() };
            return _settings;
        }

        private static void CancelMaintenance()
        {
            _maintenanceGeneration++;
            _maintenanceCancellation?.Cancel();
        }

        private static void CompleteMaintenance()
        {
            if (_maintenance == null || !_maintenance.IsCompleted) return;
            var completed = _maintenance;
            _maintenance = null;
            _maintenanceCancellation?.Dispose();
            _maintenanceCancellation = null;
            try
            {
                var result = completed.GetAwaiter().GetResult(); // Already completed; never waits on I/O.
                if (result.Released) _releaseQueued = false;
                if (result.Generation != _maintenanceGeneration) return;
                _status = result.Status;
                _supervisorHealthy = result.Healthy;
                _launchAlive = result.LaunchAlive;
                if (result.Error != null)
                {
                    if (result.Released) _lastReleaseJson = null; // Retry a failed release.
                    SetError(result.Error);
                }
                else _lastError = null;
                if (result.Started) Debug.Log(LogPrefix + "Starting the shared server supervisor.");
            }
            catch (OperationCanceledException) { _releaseQueued = false; }
            catch (Exception exception) { _releaseQueued = false; SetError(exception.Message); }
        }

        private static void QueueMaintenance()
        {
            CompleteMaintenance();
            if (_releaseQueued || Lifetime.IsCancellationRequested) return;
            try
            {
                var settings = CaptureSettings();
                if (_maintenance != null)
                {
                    if (!ReferenceEquals(settings, _activeSettings) &&
                        _maintenanceCancellation != null && !_maintenanceCancellation.IsCancellationRequested)
                        CancelMaintenance();
                    return;
                }
                InitializeIdentity();
                int generation = _maintenanceGeneration;
                _maintenanceCancellation = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
                var token = _maintenanceCancellation.Token;
                _activeSettings = settings;
                _lastReleaseJson = null;
                _maintenance = Task.Run(() =>
                {
                    var result = new MaintenanceResult { Generation = generation };
                    try
                    {
                        WriteLease(settings, token);
                        result.Status = ReadStatusFromDisk();
                        result.Healthy = CheckSupervisor(result.Status);
                        if (!result.Healthy) result.Started = EnsureSupervisor(settings, token);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception) { result.Error = exception.Message; }
                    try { result.LaunchAlive = _launchProcess != null && !_launchProcess.HasExited; }
                    catch { result.LaunchAlive = false; }
                    return result;
                }, token);
            }
            catch (Exception exception) { SetError(exception.Message); }
        }

        private static bool CheckSupervisor(SupervisorStatus status)
        {
            if (!StatusIsFresh(status)) return false;
            try
            {
                using (var process = Process.GetProcessById(status.supervisorPid)) return !process.HasExited;
            }
            catch { return false; }
        }

        private static void InitializeIdentity()
        {
            if (_leasePath != null) return;
            using (var process = Process.GetCurrentProcess())
            {
                _processId = process.Id;
                _processStart = ProcessIdentity.StartFileTimeUtc(process).ToString(CultureInfo.InvariantCulture);
            }
            _leasePath = Path.Combine(StateDirectory, "editors", _processId + "-" + _processStart + ".json");
        }

        // These methods run only on the serial background maintenance worker.
        private static void WriteLease(ServerSettings settings, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (settings.Executable == null)
                settings.Executable = ResolveExecutable(string.IsNullOrWhiteSpace(settings.UvxOverride) ? "uvx" : settings.UvxOverride);
            if (settings.Executable == null)
                throw new InvalidOperationException("uvx was not found. Install uv or correct the configured uvx path.");
            var lease = new EditorLease { processId = _processId, processStartFileTimeUtc = _processStart,
                projectPath = ProjectPath, baseUrl = settings.Url, executable = settings.Executable,
                arguments = settings.Arguments, packageVersion = settings.Version };
            string json = JsonUtility.ToJson(lease, true);
            token.ThrowIfCancellationRequested();
            if (json == _lastLeaseJson && File.Exists(_leasePath)) return;
            AtomicWrite(_leasePath, json);
            _lastLeaseJson = json;
            _lastReleaseJson = null;
        }

        private static bool EnsureSupervisor(ServerSettings settings, CancellationToken token)
        {
            if (DateTime.UtcNow < _nextSpawnUtc) return false;
            _nextSpawnUtc = DateTime.UtcNow.AddSeconds(15);
            var uv = ResolveExecutable(Path.Combine(Path.GetDirectoryName(settings.Executable),
                "uv" + Path.GetExtension(settings.Executable)));
            if (uv == null) throw new InvalidOperationException("uv was not found alongside uvx. Install the complete uv runtime.");
            if (_scriptPath == null || !File.Exists(_scriptPath))
            {
                if (settings.Version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || settings.Version.Contains(".."))
                    throw new InvalidOperationException("The Launcher package version is invalid.");
                string source = Path.Combine(settings.Root, "Editor", "Supervisor", "supervisor.py");
                string destination = Path.Combine(StateDirectory, "runtime", settings.Version, "supervisor.py");
                string text = File.ReadAllText(source);
                token.ThrowIfCancellationRequested();
                if (!File.Exists(destination) || File.ReadAllText(destination) != text) AtomicWrite(destination, text);
                _scriptPath = destination;
            }
            var info = new ProcessStartInfo { FileName = uv,
                Arguments = JoinArguments(new[] { "run", "--no-project", "--python", ">=3.10", _scriptPath, "--state-dir", StateDirectory }),
                WorkingDirectory = StateDirectory, UseShellExecute = false, CreateNoWindow = true };
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(StateDirectory);
            _launchProcess?.Dispose();
            _launchProcess = Process.Start(info);
            if (_launchProcess == null) throw new InvalidOperationException("Could not start the shared MCP server supervisor.");
            return true;
        }

        private static void TryConnect()
        {
            if (_connectInFlight || Lifetime.IsCancellationRequested || EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;
            var client = MCPServiceLocator.TransportManager.GetClient(TransportMode.Http);
            if (client is ManagedHttpTransportClient managed && managed.NeedsVerification &&
                !_verificationInFlight && EditorApplication.timeSinceStartup >= _nextVerify && !_needsNewEndpoint)
                _ = VerifyConnectionAsync();
            if (!_needsNewEndpoint && client != null && client.IsConnected)
            {
                _connectedOnce = true;
                _connectFailures = 0;
                _lastConnectionError = null;
                _disconnectedSince = -1;
                return;
            }
            // Explicit Stop/ForceStop clears the transport error and cancels upstream
            // reconnection. Resume automatic management in that case.
            if (client == null || (!client.IsConnected && string.IsNullOrEmpty(client.State?.Error)))
            {
                _connectedOnce = false;
                _disconnectedSince = -1;
            }
            if (_connectedOnce)
            {
                if (_disconnectedSince == -1) _disconnectedSince = EditorApplication.timeSinceStartup;
                // Give upstream a full reconnect cycle before rebuilding only this
                // project's client. A dead reconnect task must not block it forever.
                if (EditorApplication.timeSinceStartup - _disconnectedSince < 60) return;
                MCPServiceLocator.TransportManager.ForceStop(TransportMode.Http);
                _connectedOnce = false;
                _disconnectedSince = -1;
                SetConnectionError("Upstream reconnection did not recover this project within 60 seconds. Rebuilding its connection.");
            }
            if (EditorApplication.timeSinceStartup < _nextConnect) return;
            // The managed adapter waits asynchronously for MCP HTTP readiness
            // before starting the real WebSocket handshake and registration.
            _ = ConnectAsync(Lifetime.Token);
        }

        private static async Task VerifyConnectionAsync()
        {
            _verificationInFlight = true;
            int generation = _connectionGeneration;
            string url = BaseUrl;
            try
            {
                bool verified = await MCPServiceLocator.TransportManager.VerifyAsync(TransportMode.Http);
                if (generation != _connectionGeneration || !CanManageLocalServer || url != BaseUrl) return;
                if (verified) _lastVerifiedUtc = DateTimeOffset.UtcNow.ToString("u", CultureInfo.InvariantCulture);
                else SetConnectionError("Project verification failed. Waiting for reconnection before rebuilding this project's client.");
            }
            catch (Exception exception)
            {
                if (generation == _connectionGeneration && CanManageLocalServer) SetConnectionError(exception.Message);
            }
            finally
            {
                _nextVerify = EditorApplication.timeSinceStartup + 10;
                _verificationInFlight = false;
            }
        }

        private static void CancelPendingConnection()
        {
            var client = MCPServiceLocator.TransportManager.GetClient(TransportMode.Http);
            if (client is ManagedHttpTransportClient managed) managed.CancelPendingLocalStart();
            else if (_connectInFlight && !IsRemote) MCPServiceLocator.TransportManager.ForceStop(TransportMode.Http);
        }

        private static async Task ConnectAsync(CancellationToken token)
        {
            _connectInFlight = true;
            int generation = _connectionGeneration;
            try
            {
                string requestedUrl = BaseUrl;
                if (token.IsCancellationRequested || !Enabled || IsRemote) return;
                bool connected = await MCPServiceLocator.Bridge.StartAsync();
                if (token.IsCancellationRequested || generation != _connectionGeneration) return;
                if (!Enabled || IsRemote)
                {
                    return;
                }
                if (BaseUrl != requestedUrl)
                {
                    _connectedOnce = false;
                    _needsNewEndpoint = true;
                    _nextConnect = 0;
                    return;
                }
                _connectedOnce = connected;
                if (connected)
                {
                    _needsNewEndpoint = false;
                    _connectFailures = 0;
                    _disconnectedSince = -1;
                    _nextVerify = 0;
                    _lastVerifiedUtc = DateTimeOffset.UtcNow.ToString("u", CultureInfo.InvariantCulture);
                    _lastConnectionError = null;
                    Debug.Log(LogPrefix + "The Unity project automatically connected to " + requestedUrl + ".");
                }
                else
                {
                    _connectFailures++;
                    _nextConnect = EditorApplication.timeSinceStartup + Math.Min(30, 5 * _connectFailures);
                    var client = MCPServiceLocator.TransportManager.GetClient(TransportMode.Http) as ManagedHttpTransportClient;
                    SetConnectionError(client?.LastStartError ?? "The Unity project is not connected to the MCP server. Retrying automatically.");
                }
            }
            catch (Exception exception)
            {
                if (!token.IsCancellationRequested && generation == _connectionGeneration)
                {
                    _connectFailures++;
                    _nextConnect = EditorApplication.timeSinceStartup + Math.Min(30, 5 * _connectFailures);
                    SetConnectionError(exception.Message);
                }
            }
            finally { _connectInFlight = false; }
        }

        private static void BeforeReload()
        {
            Lifetime.Cancel();
            CancelMaintenance();
            EditorApplication.update -= Update;
            if (_connectInFlight && !IsRemote)
                MCPServiceLocator.TransportManager.ForceStop(TransportMode.Http);
            // Keep the lease while this Unity process reloads scripts.
        }

        private static void OnQuitting()
        {
            _quitting = true;
            Lifetime.Cancel();
            RemoveLease();
            // Keep the service wrapper installed through upstream quit callbacks.
        }

        private static void RemoveLease()
        {
            // Queue the tombstone AFTER every prior active write. This task does not need
            // UnitySynchronizationContext and can finish while the Editor is quitting.
            if (_releaseQueued || (_lastReleaseJson != null && _maintenance == null)) return;
            InitializeIdentity();
            CancelMaintenance();
            string json = JsonUtility.ToJson(new EditorLease { processId = _processId,
                processStartFileTimeUtc = _processStart, released = true }, true);
            _lastReleaseJson = json;
            _releaseQueued = true;
            var previous = _maintenance;
            int generation = _maintenanceGeneration;
            _maintenance = Task.Run(async () =>
            {
                if (previous != null) { try { await previous.ConfigureAwait(false); } catch { } }
                var result = new MaintenanceResult { Generation = generation, Released = true };
                try { AtomicWrite(_leasePath, json); _lastLeaseJson = null; _lastReleaseJson = json; }
                catch (Exception exception) { result.Error = "Could not unregister this Editor: " + exception.Message; }
                return result;
            });
        }

        internal static SupervisorStatus ReadStatus() => _status;

        private static SupervisorStatus ReadStatusFromDisk()
        {
            try
            {
                string path = Path.Combine(StateDirectory, "status.json");
                if (!File.Exists(path)) return null;
                var status = JsonUtility.FromJson<SupervisorStatus>(File.ReadAllText(path));
                return status?.schemaVersion == 1 ? status : null;
            }
            catch { return null; }
        }

        internal static void ReportSharedStop()
        {
            Debug.Log(LogPrefix + "The shared server stops after the last managed Unity Editor closes. Disable this project's automatic management in the Launcher window.");
        }

        internal static void ReportLaunchFailure()
        {
            SetError(_lastError ?? "The shared server is not ready. Check the log directory in the Launcher window.");
        }

        private static void SetError(string error)
        {
            if (_lastError == error) return;
            _lastError = error;
            Debug.LogWarning(LogPrefix + error);
        }

        private static void SetConnectionError(string error)
        {
            if (_lastConnectionError == error) return;
            _lastConnectionError = error;
            Debug.LogWarning(LogPrefix + error);
        }

        private static bool IsAllowedUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/") return false;
            return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
        }

        private static string ResolveExecutable(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim().Trim('"');
            if (Path.IsPathRooted(value)) return File.Exists(value) ? Path.GetFullPath(value) : null;
            bool windows = Path.DirectorySeparatorChar == '\\';
            var extensions = windows ? new[] { "", ".exe" } : new[] { "" };
            var directories = new List<string>((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            directories.Add(Path.Combine(userProfile, ".local", "bin"));
            directories.Add(Path.Combine(userProfile, ".cargo", "bin"));
            if (windows)
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                directories.Add(Path.Combine(local, "Programs", "uv"));
                directories.Add(Path.Combine(local, "Microsoft", "WinGet", "Links"));
                directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "uv"));
            }
            else directories.AddRange(new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin" });
            foreach (string directory in directories)
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                foreach (string extension in extensions)
                {
                    string candidate = Path.Combine(directory.Trim().Trim('"'), value + extension);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
            }
            return null;
        }

        internal static string JoinArguments(IEnumerable<string> arguments)
        {
            var result = new List<string>();
            foreach (string argument in arguments)
            {
                var quoted = new StringBuilder("\"");
                int backslashes = 0;
                foreach (char character in argument)
                {
                    if (character == '\\') { backslashes++; continue; }
                    quoted.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
                    quoted.Append(character);
                    backslashes = 0;
                }
                quoted.Append('\\', backslashes * 2);
                quoted.Append('"');
                result.Add(quoted.ToString());
            }
            return string.Join(" ", result);
        }

        private static void AtomicWrite(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static string Hash(string value)
        {
            using (var algorithm = SHA256.Create())
                return BitConverter.ToString(algorithm.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
        }

        [Serializable]
#pragma warning disable CS0649
        private sealed class PackageMetadata
        {
            public string version;
        }
#pragma warning restore CS0649
    }
}

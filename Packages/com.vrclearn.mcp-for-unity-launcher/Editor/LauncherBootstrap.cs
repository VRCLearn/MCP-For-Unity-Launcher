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
using MCPForUnity.Editor.Services.Server;
using MCPForUnity.Editor.Services.Transport;
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
        private static int _processId;
        private static string _processStart;
        private static double _nextTick;
        private static double _nextSpawn;
        private static double _nextConnect;
        private static int _connectFailures;
        private static bool _connectInFlight;
        private static bool _connectedOnce;
        private static bool _needsNewEndpoint;
        private static string _lastLocalUrl;
        private static bool _quitting;
        private static Process _launchProcess;

        internal static string LastError => _lastError;
        internal static string LogDirectory => StateDirectory;
        internal static string LaunchLogPath => Path.Combine(StateDirectory, "supervisor.log");
        internal static bool HasSupervisorLaunch => _launchProcess != null;
        internal static bool IsSupervisorLaunchAlive
        {
            get
            {
                try { if (_launchProcess != null && !_launchProcess.HasExited) return true; }
                catch { }
                return IsSupervisorHealthy;
            }
        }
        internal static bool Enabled
        {
            get => EditorPrefs.GetBool(PreferenceKey, true);
            set
            {
                if (Enabled == value) return;
                EditorPrefs.SetBool(PreferenceKey, value);
                if (value)
                {
                    InstallWrapper();
                    _connectedOnce = false;
                    _nextTick = 0;
                    _nextConnect = 0;
                }
                else
                {
                    if (_connectInFlight)
                        MCPServiceLocator.TransportManager.ForceStop(TransportMode.Http);
                    RemoveLease();
                    RestoreWrapper();
                    _connectedOnce = false;
                }
            }
        }

        internal static bool IsRemote => HttpEndpointUtility.IsRemoteScope();
        internal static string BaseUrl => HttpEndpointUtility.GetLocalBaseUrl();
        internal static bool CanManageLocalServer => Enabled && !IsRemote && IsAllowedUrl(BaseUrl);
        internal static bool IsSupervisorHealthy
        {
            get
            {
                var status = ReadStatus();
                if (status == null || status.supervisorPid <= 0 || !DateTimeOffset.TryParse(
                        status.updatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var updated))
                    return false;
                var age = DateTimeOffset.UtcNow - updated;
                if (age.TotalSeconds < -5 || age.TotalSeconds > 15) return false;
                try
                {
                    using (var process = Process.GetProcessById(status.supervisorPid))
                        return !process.HasExited;
                }
                catch { return false; }
            }
        }

        static LauncherBootstrap()
        {
            // Protect upstream startup and quitting before deferred editor work runs.
            if (!Application.isBatchMode && Enabled) InstallWrapper();
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

        private static void Update()
        {
            if (_quitting || EditorApplication.timeSinceStartup < _nextTick) return;
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
                if (IsRemote)
                {
                    RemoveLease();
                    _connectedOnce = false;
                    _needsNewEndpoint = true;
                    return;
                }
                if (!IsAllowedUrl(BaseUrl))
                    throw new InvalidOperationException("本地 MCP 地址必须是回环 HTTP 地址，例如 http://127.0.0.1:8080。");
                EditorConfigurationCache.Instance.SetUseHttpTransport(true);
                if (_lastLocalUrl != null && _lastLocalUrl != BaseUrl)
                {
                    _connectedOnce = false;
                    _needsNewEndpoint = true;
                    _nextConnect = 0;
                }
                _lastLocalUrl = BaseUrl;
                WriteLease();
                EnsureSupervisor();
                TryConnect();
            }
            catch (Exception exception) { SetError(exception.Message); }
        }

        internal static bool RequestServerStart()
        {
            if (!CanManageLocalServer) return false;
            try
            {
                WriteLease();
                EnsureSupervisor();
                return true;
            }
            catch (Exception exception)
            {
                SetError(exception.Message);
                return false;
            }
        }

        private static void InitializeIdentity()
        {
            if (_leasePath != null) return;
            using (var process = Process.GetCurrentProcess())
            {
                _processId = process.Id;
                _processStart = process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);
            }
            _leasePath = Path.Combine(StateDirectory, "editors", _processId + "-" + _processStart + ".json");
        }

        private static void WriteLease()
        {
            InitializeIdentity();
            ResolveScript();
            var uvx = ResolveExecutable(MCPServiceLocator.Paths.GetUvxPath());
            if (uvx == null)
                throw new InvalidOperationException("未找到 uvx。请安装 uv，并在 MCP for Unity 的高级设置中配置 uvx 路径。");
            var arguments = new List<string>(AssetPathUtility.GetUvxDevFlagsList());
            arguments.AddRange(AssetPathUtility.GetBetaServerFromArgsList());
            arguments.AddRange(new[] { "mcp-for-unity", "--transport", "http", "--http-url", BaseUrl });
            if (EditorPrefs.GetBool("MCPForUnity.ProjectScopedTools.LocalHttp", true))
                arguments.Add("--project-scoped-tools");
            var lease = new EditorLease
            {
                processId = _processId,
                processStartFileTimeUtc = _processStart,
                projectPath = ProjectPath,
                baseUrl = BaseUrl,
                executable = uvx,
                arguments = arguments.ToArray(),
                packageVersion = _packageVersion
            };
            string json = JsonUtility.ToJson(lease, true);
            if (json == _lastLeaseJson && File.Exists(_leasePath)) return;
            AtomicWrite(_leasePath, json);
            _lastLeaseJson = json;
            _lastReleaseJson = null;
        }

        private static void ResolveScript()
        {
            if (_scriptPath != null && File.Exists(_scriptPath)) return;
            var package = PackageInfo.FindForAssembly(typeof(LauncherBootstrap).Assembly);
            string root = package?.resolvedPath;
            _packageVersion = package?.version;
            if (string.IsNullOrEmpty(root))
            {
                foreach (var guid in AssetDatabase.FindAssets("LauncherBootstrap t:MonoScript"))
                {
                    string asset = AssetDatabase.GUIDToAssetPath(guid);
                    if (Path.GetFileName(asset) != "LauncherBootstrap.cs") continue;
                    root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(asset), ".."));
                    break;
                }
                if (root != null)
                {
                    var metadata = JsonUtility.FromJson<PackageMetadata>(File.ReadAllText(Path.Combine(root, "package.json")));
                    _packageVersion = metadata.version;
                }
            }
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(_packageVersion))
                throw new InvalidOperationException("无法定位 Launcher 包及版本，请检查 VPM 安装。");
            if (_packageVersion.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || _packageVersion.Contains(".."))
                throw new InvalidOperationException("Launcher 包版本格式无效。");
            string source = Path.Combine(root, "Editor", "Supervisor", "supervisor.py");
            if (!File.Exists(source)) throw new FileNotFoundException("Launcher 包缺少 supervisor.py，请重新安装包。");
            string destination = Path.Combine(StateDirectory, "runtime", _packageVersion, "supervisor.py");
            string text = File.ReadAllText(source);
            if (!File.Exists(destination) || File.ReadAllText(destination) != text) AtomicWrite(destination, text);
            _scriptPath = destination;
        }

        private static void EnsureSupervisor()
        {
            if (IsSupervisorHealthy || EditorApplication.timeSinceStartup < _nextSpawn) return;
            _nextSpawn = EditorApplication.timeSinceStartup + 15;
            var uvx = ResolveExecutable(MCPServiceLocator.Paths.GetUvxPath());
            var uv = uvx == null ? null : ResolveExecutable(new ServerCommandBuilder().BuildUvPathFromUvx(uvx));
            if (uv == null)
                throw new InvalidOperationException("未找到 uv（需要与 uvx 位于同一目录）。请安装完整 uv 或修正 uvx 路径。");
            ResolveScript();
            var info = new ProcessStartInfo
            {
                FileName = uv,
                Arguments = JoinArguments(new[] { "run", "--no-project", "--python", ">=3.10", _scriptPath, "--state-dir", StateDirectory }),
                WorkingDirectory = StateDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Directory.CreateDirectory(StateDirectory);
            _launchProcess?.Dispose();
            _launchProcess = Process.Start(info);
            if (_launchProcess == null) throw new InvalidOperationException("无法启动 MCP 共享服务守护进程。");
            Debug.Log(LogPrefix + "正在启动共享服务守护进程。");
        }

        private static void TryConnect()
        {
            if (_connectInFlight || Lifetime.IsCancellationRequested || EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;
            var client = MCPServiceLocator.TransportManager.GetClient(TransportMode.Http);
            if (!_needsNewEndpoint && client != null && client.IsConnected)
            {
                _connectedOnce = true;
                _connectFailures = 0;
                _lastError = null;
                return;
            }
            // Once connected, upstream owns its indefinite socket reconnection loop.
            if (_connectedOnce || EditorApplication.timeSinceStartup < _nextConnect) return;
            if (!_wrapper.Original.IsLocalHttpServerReachable()) return;
            _ = ConnectAsync(Lifetime.Token);
        }

        private static async Task ConnectAsync(CancellationToken token)
        {
            _connectInFlight = true;
            try
            {
                if (token.IsCancellationRequested || !Enabled || IsRemote) return;
                bool connected = await MCPServiceLocator.Bridge.StartAsync();
                if (token.IsCancellationRequested) return;
                if (!Enabled || IsRemote)
                {
                    await MCPServiceLocator.TransportManager.StopAsync(TransportMode.Http);
                    return;
                }
                _connectedOnce = connected;
                if (connected)
                {
                    _needsNewEndpoint = false;
                    _connectFailures = 0;
                    _lastError = null;
                }
                else
                {
                    _connectFailures++;
                    _nextConnect = EditorApplication.timeSinceStartup + Math.Min(30, 5 * _connectFailures);
                    SetError("MCP 服务已启动，但编辑器连接失败；将自动重试。");
                }
            }
            catch (Exception exception)
            {
                if (!token.IsCancellationRequested)
                {
                    _connectFailures++;
                    _nextConnect = EditorApplication.timeSinceStartup + Math.Min(30, 5 * _connectFailures);
                    SetError(exception.Message);
                }
            }
            finally { _connectInFlight = false; }
        }

        private static void BeforeReload()
        {
            Lifetime.Cancel();
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
            try
            {
                // A reload may switch directly to remote or disable management before
                // its first tick. Resolve our identity even if this domain wrote no lease.
                InitializeIdentity();
                var release = new EditorLease
                {
                    processId = _processId,
                    processStartFileTimeUtc = _processStart,
                    released = true
                };
                string json = JsonUtility.ToJson(release, true);
                _lastLeaseJson = null;
                if (_lastReleaseJson == json && File.Exists(_leasePath)) return;
                AtomicWrite(_leasePath, json);
                _lastReleaseJson = json;
            }
            catch (Exception exception) { SetError("无法注销当前编辑器：" + exception.Message); }
        }

        internal static SupervisorStatus ReadStatus()
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
            Debug.Log(LogPrefix + "共享服务会在最后一个受管理的 Unity 编辑器退出后关闭。可在 Launcher 窗口关闭当前项目的自动管理。");
        }

        internal static void ReportLaunchFailure()
        {
            SetError(_lastError ?? "共享服务尚未就绪，请在 Launcher 窗口查看日志目录。");
        }

        private static void SetError(string error)
        {
            if (_lastError == error) return;
            _lastError = error;
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
            var extensions = Application.platform == RuntimePlatform.WindowsEditor
                ? new[] { "", ".exe" } : new[] { "" };
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
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

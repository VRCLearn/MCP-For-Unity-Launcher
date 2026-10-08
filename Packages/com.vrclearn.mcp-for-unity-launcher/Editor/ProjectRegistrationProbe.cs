using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services;
using UnityEngine;

namespace MCPForUnityLauncher.Editor
{
    internal static class ProjectRegistrationProbe
    {
        internal static async Task<bool> WaitForServerAsync(CancellationToken token)
        {
            string url = LauncherBootstrap.BaseUrl.TrimEnd('/') + "/health";
            while (true)
            {
                token.ThrowIfCancellationRequested();
                using (var request = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    request.CancelAfter(TimeSpan.FromSeconds(1));
                    try
                    {
                        var health = JsonUtility.FromJson<Health>(await RequestAsync(url, null, request.Token));
                        if (health?.status == "healthy" && health.message == "MCP for Unity server is running")
                            return true;
                    }
                    catch (Exception exception) when (exception is WebException || exception is IOException ||
                        exception is ArgumentException || exception is OperationCanceledException)
                    {
                        token.ThrowIfCancellationRequested();
                    }
                }
                await Task.Delay(500, token);
            }
        }

        internal static async Task<string> VerifyAsync(CancellationToken token)
        {
            // Capture Unity APIs on the calling Editor thread before any I/O.
            string url = LauncherBootstrap.BaseUrl.TrimEnd('/');
            string assets = Application.dataPath;
            string root = Path.GetFullPath(Path.Combine(assets, ".."));
            string hash;
            // MCP for Unity 10.3 uses SHA1(Application.dataPath), truncated to 16 hex digits.
            using (var sha = SHA1.Create())
                hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(assets)))
                    .Replace("-", "").Substring(0, 16).ToLowerInvariant();
            string[] expected = MCPServiceLocator.ToolDiscovery.GetEnabledTools().Select(t => t.Name).ToArray();
            Exception last = null;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var instances = JsonUtility.FromJson<Instances>(await RequestAsync(url + "/api/instances", null, token));
                    var instance = instances?.instances?.SingleOrDefault(i => i.hash == hash);
                    if (instances?.success != true || string.IsNullOrEmpty(instance?.session_id))
                        throw new InvalidOperationException("This project has no registered MCP session.");
                    var tools = JsonUtility.FromJson<Tools>(await RequestAsync(url + "/api/custom-tools?instance=" + hash, null, token));
                    if (tools?.success != true || tools.project_id != hash || tools.tools == null ||
                        expected.Any(name => !tools.tools.Any(t => t.name == name)))
                        throw new InvalidOperationException("This project's MCP tools are not fully registered.");
                    string command = "{\"type\":\"get_project_info\",\"params\":{},\"unity_instance\":\"" + hash + "\"}";
                    var reply = JsonUtility.FromJson<Reply>(await RequestAsync(url + "/api/command", command, token));
                    var comparison = Application.platform == RuntimePlatform.WindowsEditor
                        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    if (reply?.status != "success" || reply.result?.success != true ||
                        string.IsNullOrEmpty(reply.result.data?.projectRoot) ||
                        !string.Equals(Path.GetFullPath(reply.result.data.projectRoot), root, comparison))
                        throw new InvalidOperationException("The MCP project round trip did not confirm this Editor.");
                    // Do not confirm a session that disappeared during the round trip.
                    var final = JsonUtility.FromJson<Instances>(await RequestAsync(url + "/api/instances", null, token));
                    if (final?.success == true && final.instances?.Any(i => i.hash == hash && i.session_id == instance.session_id) == true)
                        return instance.session_id;
                    throw new InvalidOperationException("The MCP session changed during verification.");
                }
                catch (Exception exception) when (!(exception is OperationCanceledException))
                {
                    last = exception;
                }
                try { await Task.Delay(500, token); }
                catch (OperationCanceledException) { throw new OperationCanceledException(last?.Message, last, token); }
            }
        }

        private static async Task<string> RequestAsync(string url, string body, CancellationToken token)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Proxy = null;
            request.AllowAutoRedirect = false;
            request.Method = body == null ? "GET" : "POST";
            using (token.Register(request.Abort))
            {
                if (body != null)
                {
                    request.ContentType = "application/json";
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    request.ContentLength = bytes.Length;
                    using (var stream = await request.GetRequestStreamAsync())
                        await stream.WriteAsync(bytes, 0, bytes.Length, token);
                }
                using (var response = await request.GetResponseAsync())
                using (var stream = response.GetResponseStream())
                using (var buffer = new MemoryStream())
                {
                    var bytes = new byte[8192];
                    int count;
                    while ((count = await stream.ReadAsync(bytes, 0, bytes.Length, token)) > 0)
                    {
                        if (buffer.Length + count > 1024 * 1024) throw new InvalidDataException("MCP verification response is too large.");
                        buffer.Write(bytes, 0, count);
                    }
                    return Encoding.UTF8.GetString(buffer.ToArray());
                }
            }
        }

#pragma warning disable CS0649
        [Serializable] private sealed class Health { public string status; public string message; }
        [Serializable] private sealed class Instances { public bool success; public Instance[] instances; }
        [Serializable] private sealed class Instance { public string hash; public string session_id; }
        [Serializable] private sealed class Tools { public bool success; public string project_id; public Tool[] tools; }
        [Serializable] private sealed class Tool { public string name; }
        [Serializable] private sealed class Reply { public string status; public Result result; }
        [Serializable] private sealed class Result { public bool success; public Data data; }
        [Serializable] private sealed class Data { public string projectRoot; }
#pragma warning restore CS0649
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RstcUpdater
{
    /// <summary>
    /// MC 原版下载镜像：自动把 Mojang 请求改写为 BMCLAPI；镜像不可用（异常/404/5xx）时回退官方地址。
    /// 只处理 GET 且无请求体的请求；GitHub 等其他域名原样通过。
    /// </summary>
    internal sealed class MirrorHttpHandler : DelegatingHandler
    {
        private const string Bmcl = "https://bmclapi2.bangbang93.com";
        private static readonly string LogPath = Environment.GetEnvironmentVariable("RSTC_MIRROR_LOG");
        private static string _lastVersion;

        public MirrorHttpHandler(HttpMessageHandler inner)
            : base(inner)
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get || request.Content != null)
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            Uri mirror = Map(request.RequestUri);
            if (mirror == null)
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                using (var copy = CloneGet(request, mirror))
                {
                    Log("镜像 " + request.RequestUri + " -> " + mirror);
                    HttpResponseMessage response = await base.SendAsync(copy, cancellationToken).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        if (IsVersionJson(mirror))
                        {
                            _lastVersion = Path.GetFileName(Path.GetDirectoryName(mirror.AbsolutePath));
                        }

                        return response;
                    }

                    Log("镜像 HTTP " + (int)response.StatusCode + "，回退官方：" + request.RequestUri);
                    response.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log("镜像失败（" + ex.GetType().Name + "），回退官方：" + request.RequestUri);
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        private static HttpRequestMessage CloneGet(HttpRequestMessage source, Uri uri)
        {
            var copy = new HttpRequestMessage(HttpMethod.Get, uri);
            foreach (KeyValuePair<string, IEnumerable<string>> header in source.Headers)
            {
                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return copy;
        }

        private static bool IsVersionJson(Uri mirror)
        {
            return mirror != null &&
                   mirror.AbsolutePath.StartsWith("/version/", StringComparison.OrdinalIgnoreCase) &&
                   mirror.AbsolutePath.EndsWith("/json", StringComparison.OrdinalIgnoreCase);
        }

        private static Uri Map(Uri uri)
        {
            if (uri == null)
            {
                return null;
            }

            string host = uri.Host.ToLowerInvariant();
            string path = uri.AbsolutePath;
            if (host == "launchermeta.mojang.com" || host == "launcher.mojang.com")
            {
                if (path.EndsWith("version_manifest_v2.json", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith("version_manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    return new Uri(Bmcl + "/mc/game/version_manifest_v2.json");
                }

                return null;
            }

            if (host == "piston-meta.mojang.com")
            {
                if (path.EndsWith("version_manifest_v2.json", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith("version_manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    return new Uri(Bmcl + "/mc/game/version_manifest_v2.json");
                }

                if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    bool allDigits = name.Length > 0 && name.All(char.IsDigit);
                    if (!allDigits)
                    {
                        return new Uri(Bmcl + "/version/" + name + "/json");
                    }
                }

                return null;
            }

            if (host == "piston-data.mojang.com")
            {
                if (path.EndsWith("/client.jar", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(_lastVersion))
                {
                    return new Uri(Bmcl + "/version/" + _lastVersion + "/client");
                }

                return null;
            }

            return null;
        }

        private static void Log(string message)
        {
            if (string.IsNullOrEmpty(LogPath))
            {
                return;
            }

            try
            {
                File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
            catch (Exception)
            {
            }
        }
    }
}

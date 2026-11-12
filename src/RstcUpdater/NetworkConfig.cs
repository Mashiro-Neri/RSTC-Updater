using System;
using System.Net;
using System.Net.Http;

namespace RstcUpdater
{
    /// <summary>统一网络设置：可选 HTTP 代理 + HttpClient 工厂（代理只影响下载，不影响本地逻辑）。</summary>
    internal static class NetworkConfig
    {
        /// <summary>形如 http://127.0.0.1:7890；空表示直连。</summary>
        public static string ProxyUrl;

        /// <summary>MC 原版是否优先走 BMCLAPI 镜像（失败自动回退官方）；RSTC_NO_MIRROR=1 可关闭。</summary>
        public static bool UseMirror = Environment.GetEnvironmentVariable("RSTC_NO_MIRROR") != "1" &&
                                        Environment.GetEnvironmentVariable("RSTC_NO_MIRROR") != "true";

        public static HttpClient CreateHttpClient(TimeSpan timeout)
        {
            var handler = new HttpClientHandler();
            if (!string.IsNullOrWhiteSpace(ProxyUrl))
            {
                try
                {
                    handler.Proxy = new WebProxy(ProxyUrl.Trim());
                    handler.UseProxy = true;
                }
                catch (Exception)
                {
                }
            }

            HttpMessageHandler top = handler;
            if (UseMirror)
            {
                top = new MirrorHttpHandler(handler);
            }

            return new HttpClient(top) { Timeout = timeout };
        }
    }
}

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

            return new HttpClient(handler) { Timeout = timeout };
        }
    }
}

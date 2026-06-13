using System.Net;
using Yarp.ReverseProxy.Forwarder;

namespace Ede.Uofx.Customize.Web.Core.Middlewares
{
    /// <summary>
    /// 開發環境專用：反向代理到 Angular 開發服務器（類似 Nginx）
    /// </summary>
    /// <remarks>
    /// 目的：除了 API 路由外，所有請求都轉發到 Angular 開發服務器
    /// （ng serve 的位址由 ANGULAR_SPA_PROXY_URL 決定，預設 http://localhost:40001；以下範例以 {ng-serve} 代稱）
    ///
    /// 轉發引擎：微軟官方 YARP IHttpForwarder（direct forwarding 模式）
    /// - 內建處理 HTTP method、request/response headers、body streaming、WebSocket 升級
    /// - 本專案特有的「路徑改寫」與「no-cache」由 DevPathTransformer 負責
    ///
    /// 【情境範例】
    /// 外部系統（UOFX）透過 PluginService 呼叫（{host} 為本 .NET host 對外位址）：
    /// 1. http://{host}/plugin.versions.json
    ///    -> 代理到 {ng-serve}/plugin.versions.json
    /// 2. http://{host}/1_0/assets/configs/fields-design.json
    ///    -> 移除版本號後代理到 {ng-serve}/assets/configs/fields-design.json
    /// 3. http://{host}/1_0/remoteEntry.js
    ///    -> 移除版本號後代理到 {ng-serve}/remoteEntry.js
    /// 4. http://{host}/1_0/api/employee
    ///    -> /api/ 開頭走後端 Controller，不轉發
    ///
    /// 【HMR WebSocket】
    /// webpack.config.js 以「決定鏈」設定 client.webSocketURL(HMR_WEBSOCKET_URL → VS_TUNNEL_URL
    /// → ASPNETCORE_URLS → page origin),讓 /ng-cli-ws WebSocket 連回本 .NET host 或 tunnel
    /// (微前端掛入 UOFX 頁面時 page origin 是 UOFX 端,不能用)。YARP SendAsync 偵測到 upgrade
    /// 後自動雙向轉發到 ng serve,無需手寫 pump。
    /// </remarks>
    public class DevProxyMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IHttpForwarder _forwarder;
        private readonly ILogger<DevProxyMiddleware> _logger;
        private readonly string _angularDevServerUrl;
        private readonly HttpMessageInvoker _httpClient;
        private readonly ForwarderRequestConfig _requestConfig;
        private readonly DevPathTransformer _transformer;

        public DevProxyMiddleware(
            RequestDelegate next,
            IHttpForwarder forwarder,
            IConfiguration configuration,
            ILogger<DevProxyMiddleware> logger)
        {
            _next = next;
            _forwarder = forwarder;
            _logger = logger;
            _angularDevServerUrl = Environment.GetEnvironmentVariable("ANGULAR_SPA_PROXY_URL")
                ?? "http://localhost:40001";

            // 共用 HttpMessageInvoker：middleware 為單例，建構子建立一次即可（勿每請求 new，避免 socket 耗盡）
            _httpClient = new HttpMessageInvoker(new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,                       // dev server 不該自動跟隨 redirect
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(15),
            });

            _requestConfig = new ForwarderRequestConfig
            {
                ActivityTimeout = TimeSpan.FromSeconds(100),
            };

            _transformer = new DevPathTransformer();
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value;
            if (path == null)
            {
                await _next(context);
                return;
            }

            // 先做與轉發相同的路徑正規化(去多斜線、去 /1_0/ 版本前綴、去 /_framework),再判斷是否為 API。
            // 如此 /api/... 與帶版本前綴的 /1_0/api/... 都能正確由 .NET 後端 Controller 處理,不會被轉發到 ng serve。
            var normalizedPath = DevPathTransformer.NormalizePath(path);

            // API 請求走後端 Controller，不轉發到 ng serve
            if (normalizedPath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                // 帶版本前綴(/1_0/api/...)時改寫為 /api/...,後端 attribute route(api/...)才比對得到
                if (!string.Equals(normalizedPath, path, StringComparison.Ordinal))
                {
                    context.Request.Path = normalizedPath;
                }

                await _next(context);
                return;
            }

            // 其餘（前端資源、plugin.json、remoteEntry.js、HMR WebSocket 等）交給 YARP 轉發到 ng serve。
            // 路徑改寫（版本號移除等）與 no-cache 由 DevPathTransformer 處理；
            // WebSocket 升級、header 轉發、body streaming 由 YARP 內建處理。
            var error = await _forwarder.SendAsync(
                context, _angularDevServerUrl, _httpClient, _requestConfig, _transformer);

            if (error != ForwarderError.None)
            {
                var errorFeature = context.GetForwarderErrorFeature();

                // 客戶端主動斷線(關閉分頁、重新整理)或 host 關閉中(RequestAborted,
                // 例如 shutdown 時仍掛著的 HMR WebSocket 被強制中斷)屬正常現象,降為 Debug 避免雜訊;
                // 其餘(如 ng serve 未就緒的連線失敗)維持 Warning
                if (error is ForwarderError.RequestCanceled
                        or ForwarderError.UpgradeRequestCanceled
                        or ForwarderError.UpgradeResponseCanceled
                    || context.RequestAborted.IsCancellationRequested)
                {
                    _logger.LogDebug("[Dev Proxy] 客戶端取消連線: {Error}", error);
                }
                else
                {
                    _logger.LogWarning("[Dev Proxy] 轉發失敗: {Error}; {Message}",
                        error, errorFeature?.Exception?.Message);
                }
            }
        }
    }

    /// <summary>
    /// DevProxyMiddleware 的擴展方法
    /// </summary>
    public static class DevProxyMiddlewareExtensions
    {
        /// <summary>
        /// 使用開發環境反向代理中間件
        /// </summary>
        /// <param name="builder">應用程式建構器</param>
        /// <returns></returns>
        public static IApplicationBuilder UseDevProxy(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<DevProxyMiddleware>();
        }
    }
}

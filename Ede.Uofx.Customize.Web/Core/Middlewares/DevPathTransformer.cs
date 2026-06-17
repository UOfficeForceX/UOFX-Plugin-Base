using System.Text.RegularExpressions;
using Yarp.ReverseProxy.Forwarder;

namespace Ede.Uofx.Customize.Web.Core.Middlewares
{
    /// <summary>
    /// 開發環境 reverse proxy 的「路徑改寫 + 回應標頭」轉換器(供 YARP IHttpForwarder 使用)。
    ///
    /// 只負責本專案特有的路徑改寫邏輯;轉發本身(HTTP method、request/response headers、
    /// body streaming、WebSocket 升級)全部交給 YARP IHttpForwarder 內建處理。
    ///
    /// 對應原 DevProxyMiddleware 手寫邏輯:
    ///   - 步驟一:多斜線正規化       → TransformRequestAsync
    ///   - 步驟二:移除版本號前綴      → TransformRequestAsync
    ///   - 步驟二-1:移除 _framework   → TransformRequestAsync
    ///   - no-cache headers           → TransformResponseAsync
    /// </summary>
    public class DevPathTransformer : HttpTransformer
    {
        // 移除 UOFX PluginService 組出的版本號前綴,例如 /1_0/xxx 或 /undefined/xxx → /xxx
        private static readonly Regex VersionPrefixRegex =
            new(@"^/(\d+_\d+|undefined)(/.+)$", RegexOptions.Compiled);

        // 連續多個斜線合併為單一斜線
        private static readonly Regex MultiSlashRegex =
            new(@"/+", RegexOptions.Compiled);

        /// <summary>
        /// 本專案特有的路徑正規化:① 多斜線合併 ② 移除版本號前綴(/1_0/) ③ 移除 /_framework 前綴。
        /// DevProxyMiddleware 的 /api 判斷與此處的轉發共用同一套規則,避免兩邊邏輯分歧。
        /// </summary>
        internal static string NormalizePath(string path)
        {
            // ① 多斜線正規化://plugin.versions.json → /plugin.versions.json
            if (path.Contains("//"))
            {
                path = MultiSlashRegex.Replace(path, "/");
            }

            // ② 移除版本號前綴:/1_0/remoteEntry.js → /remoteEntry.js
            //    (ng serve 的檔案結構無版本號目錄,需移除此前綴)
            path = VersionPrefixRegex.Replace(path, "$2");

            // ③ 移除 _framework 前綴:/_framework/xxx → /xxx
            if (path.StartsWith("/_framework/", StringComparison.OrdinalIgnoreCase))
            {
                path = path["/_framework".Length..];
            }

            return path;
        }

        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext,
            HttpRequestMessage proxyRequest,
            string destinationPrefix,
            CancellationToken cancellationToken)
        {
            // 先套用 YARP 預設轉換:複製 method、headers、body 等
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

            var path = NormalizePath(httpContext.Request.Path.Value ?? string.Empty);
            var query = httpContext.Request.QueryString.Value ?? string.Empty;
            proxyRequest.RequestUri = new Uri($"{destinationPrefix.TrimEnd('/')}{path}{query}");
        }

        public override ValueTask<bool> TransformResponseAsync(
            HttpContext httpContext,
            HttpResponseMessage? proxyResponse,
            CancellationToken cancellationToken)
        {
            // no-cache:避免瀏覽器快取舊版 remoteEntry / chunk 造成 ChunkLoadError
            // (開發環境 ng serve 內容會即時變更,一律不快取)
            var headers = httpContext.Response.Headers;
            headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
            headers.Pragma = "no-cache";
            headers.Expires = "0";

            return base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);
        }
    }
}

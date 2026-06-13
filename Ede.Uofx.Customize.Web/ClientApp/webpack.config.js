const proxyUrl = process.env.ANGULAR_SPA_PROXY_URL;
let port = 40001;
if (proxyUrl) {
    try {
        const url = new URL(proxyUrl);
        port = parseInt(url.port, 10) || 40001;
    } catch (e) {
        console.warn('Invalid ANGULAR_SPA_PROXY_URL, using default port 40001');
    }
}
console.log('Webpack dev server port:', port);

const initUofxPluginWebpack = require('@uofx/plugin/scripts/initial-webpack');
const baseConfig = initUofxPluginWebpack({
    production: false,
    usePort: port
});

// ─────────────────────────────────────────────────────────────
// HMR / live-reload WebSocket 連線位址「決定鏈」。
//
// 本專案是微前端:remoteEntry 會被掛入 UOFX 系統頁面,此時 page origin 是 UOFX host,
// webpack 預設與 auto:// 都會把 HMR WebSocket 推導到 UOFX 端 → 連不到。
// WebSocket 必須連回「資產來源」(本 .NET host 或 dev tunnel),由 YARP DevProxy 轉發到 ng serve。
//
// 決定鏈(由上而下,第一個資訊存在的層生效;變更環境變數後需重啟 dotnet run 才生效):
//   1) HMR_WEBSOCKET_URL:手動覆寫。devtunnel CLI 讓外部連入時必設(CLI 不注入任何環境變數):
//        $env:HMR_WEBSOCKET_URL='https://{tunnel-host}'; dotnet run
//   2) VS_TUNNEL_URL:Visual Studio(17.6+)啟用 Dev Tunnels 啟動時自動注入,零設定。
//   3) ASPNETCORE_URLS:dotnet run 時由 AngularDevServerService 注入 → 連回 .NET host
//      (掛入 UOFX 頁面時 ws://localhost 享 https 頁面的 mixed content 豁免)。
//   4) auto://0.0.0.0:0:獨立 npm start 時的安全預設(page origin = ng serve 自身,推導正確)。
//
// 注意:
// - 第 1~3 層必須用「物件形式」:字串形式會被 webpack-dev-server 以 new URL() 正規化,
//   wss 的 :443 會被當預設 port 剝除,烘入 bundle 後空 port 又會被換成「頁面的 port」而連錯。
// - 外部他人透過 tunnel 觀看(而非自己)時,tunnel 必須允許匿名:
//   devtunnel access create <id> -a(瀏覽器 WebSocket 無法附 X-Tunnel-Authorization 標頭)。
// - Chrome 142+ 對「公開頁面 → localhost」有 Local Network Access 權限提示,首次需按允許。
// - 保留官方 allowedHosts:'all'(外部 host 連入必需),只覆寫 client.webSocketURL。
// ─────────────────────────────────────────────────────────────
const HMR_WS_PATH = '/ng-cli-ws';

/** 將 http(s)/ws(s) URL 正規化為 webpack client.webSocketURL 物件形式 */
function parseWsTarget(raw) {
    // 容忍尾斜線(VS_TUNNEL_URL 結尾帶 /)與萬用 host('+'/'*' 會讓 new URL 拋例外)
    const cleaned = raw.trim().replace(/\/+$/, '')
        .replace('//+', '//localhost').replace('//*', '//localhost');
    const u = new URL(cleaned);
    const secure = u.protocol === 'https:' || u.protocol === 'wss:';
    const hostname = (u.hostname === '0.0.0.0' || u.hostname === '[::]') ? 'localhost' : u.hostname;
    return {
        protocol: secure ? 'wss:' : 'ws:',
        hostname,
        port: Number(u.port || (secure ? 443 : 80)), // port 必須明確,理由見上方注意事項
        pathname: HMR_WS_PATH,
    };
}

/** ASPNETCORE_URLS 可能是分號分隔多 URL;loopback 優先取 http(ws:// 有豁免、不依賴 dev cert 信任) */
function pickAspnetUrl(urls) {
    const list = urls.split(';').map(s => s.trim()).filter(Boolean);
    const isLoopback = u => /\/\/(localhost|127\.|\[::1\]|\+|\*|0\.0\.0\.0)/i.test(u);
    return list.find(u => u.startsWith('http://') && isLoopback(u))
        || list.find(u => u.startsWith('https://'))
        || list[0];
}

function resolveHmrWebSocketUrl() {
    if (process.env.HMR_WEBSOCKET_URL) {
        return { layer: '1:HMR_WEBSOCKET_URL', target: parseWsTarget(process.env.HMR_WEBSOCKET_URL) };
    }
    if (process.env.VS_TUNNEL_URL) {
        return { layer: '2:VS_TUNNEL_URL', target: parseWsTarget(process.env.VS_TUNNEL_URL) };
    }
    if (process.env.ASPNETCORE_URLS) {
        try {
            return { layer: '3:ASPNETCORE_URLS', target: parseWsTarget(pickAspnetUrl(process.env.ASPNETCORE_URLS)) };
        } catch (e) {
            console.warn('ASPNETCORE_URLS 解析失敗,回退 page origin:', e.message);
        }
    }
    return { layer: '4:auto(page origin)', target: `auto://0.0.0.0:0${HMR_WS_PATH}` };
}

// 印出採用的層級與結果,方便診斷「黏住的環境變數」(會經 [Angular] log 轉送到 dotnet console)
const hmrWs = resolveHmrWebSocketUrl();
console.log(`HMR webSocketURL [${hmrWs.layer}]:`, JSON.stringify(hmrWs.target));

baseConfig.devServer = {
    ...(baseConfig.devServer || {}),
    client: {
        ...((baseConfig.devServer && baseConfig.devServer.client) || {}),
        webSocketURL: hmrWs.target,
    },
};

module.exports = baseConfig;

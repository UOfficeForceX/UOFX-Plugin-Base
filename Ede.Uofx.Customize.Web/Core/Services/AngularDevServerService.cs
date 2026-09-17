using System.Diagnostics;
using System.Text;

namespace Ede.Uofx.Customize.Web.Core.Services
{
    /// <summary>
    /// 管理 Angular 開發伺服器的生命週期
    /// </summary>
    /// <remarks>
    /// 功能：
    /// 1. 啟動時：執行 ng serve --disable-host-check --port {Port}
    /// 2. 終止時：使用 taskkill /T /F 終止進程樹
    ///
    /// 優點：
    /// - 統一從 appsettings 讀取 port 配置
    /// - .NET 終止時自動清理 Angular 進程
    /// - 不需要修改 package.json
    /// </remarks>
    public class AngularDevServerService : IHostedService, IDisposable
    {
        private readonly ILogger<AngularDevServerService> _logger;
        private readonly IConfiguration _configuration;
        private readonly IHostEnvironment _environment;
        private Process? _ngProcess;
        private readonly int _port;
        private string? _clientAppPath;

        // 就緒探測用的 HttpClient(每次嘗試 2 秒逾時);static 共用避免重複建立
        private static readonly HttpClient _readinessClient = new() { Timeout = TimeSpan.FromSeconds(2) };

        // 服務關閉用:取消背景就緒等待(StopAsync / Dispose 時觸發)
        private readonly CancellationTokenSource _shutdownCts = new();

        // webpack-exposes.config.js 監控相關
        private FileSystemWatcher? _configWatcher;
        private Timer? _debounceTimer;
        private readonly SemaphoreSlim _restartLock = new(1, 1);
        private CancellationTokenSource? _restartCts;
        private bool _disposed;

        public AngularDevServerService(
            ILogger<AngularDevServerService> logger,
            IConfiguration configuration,
            IHostEnvironment environment)
        {
            _logger = logger;
            _configuration = configuration;
            _environment = environment;
            _port = GetPortFromProxyUrl();
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _clientAppPath = Path.Combine(_environment.ContentRootPath, "ClientApp");

            if (!Directory.Exists(_clientAppPath))
            {
                // fail-fast:缺 ClientApp 屬設定錯誤,直接讓 host 啟動失敗,
                // 避免之後每個前端請求才逐一轉發失敗、不易察覺根因。
                throw new DirectoryNotFoundException($"找不到 ClientApp 目錄,無法啟動 Angular 開發伺服器: {_clientAppPath}");
            }

            await StartServerAsync(cancellationToken, openBrowser: true);

            // 設定 webpack-exposes.config.js 檔案監控
            SetupConfigWatcher();
        }

        /// <summary>
        /// 啟動 Angular 開發伺服器（可重複呼叫以重啟）
        /// </summary>
        private async Task StartServerAsync(CancellationToken cancellationToken, bool openBrowser = false)
        {
            if (string.IsNullOrEmpty(_clientAppPath))
            {
                _logger.LogError("ClientApp 路徑未設定");
                return;
            }

            // 預檢:node_modules 必須完整。
            // csproj 的 DebugEnsureNodeEnv 只在「node_modules 資料夾不存在」時自動 npm install;
            // 切換分支 / 更新 package.json 後常出現「資料夾在、內容空殼」,ng serve 會以
            // 隱晦的 'ng' is not recognized 失敗,再演變成就緒等待逾時,不易察覺根因。
            var ngCmdPath = Path.Combine(_clientAppPath, "node_modules", ".bin", "ng.cmd");
            if (!File.Exists(ngCmdPath))
            {
                throw new InvalidOperationException(
                    $"node_modules 不完整(找不到 {ngCmdPath})。請先在 ClientApp 目錄執行 npm install 後再啟動。");
            }

            _logger.LogInformation("正在啟動 Angular 開發伺服器，Port: {Port}", _port);

            try
            {
                // 取得 .NET 實際監聽的 URL,注入給 ng serve 子進程的 proxy.conf.js 使用。
                // 一般「瀏覽器 → .NET host → DevProxy」流程下 ng serve 收不到 /api,此注入用不到;
                // 但若直接開 ng serve 的 port(或獨立啟動 Angular),proxy.conf.js 需要它才能把 /api 代理回後端。
                // 優先讀 --urls(IConfiguration["urls"])→ ASPNETCORE_URLS 環境變數 → 預設值。
                var aspnetCoreUrls =
                    _configuration["urls"] ??
                    Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ??
                    "http://localhost:54039";

                // 註:cmd.exe / taskkill 為 Windows 專用,本開發流程僅支援 Windows 環境。
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c chcp 65001 >nul && npm run ng -- serve --disable-host-check --port {_port}",
                    WorkingDirectory = _clientAppPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                // 將後端 URL 注入子進程環境,讓 Angular proxy.conf.js 的 env.ASPNETCORE_URLS 能正確讀取
                startInfo.Environment["ASPNETCORE_URLS"] = aspnetCoreUrls;
                _logger.LogInformation("注入 ASPNETCORE_URLS 至 Angular 子進程: {Urls}", aspnetCoreUrls);

                _ngProcess = new Process { StartInfo = startInfo };

                _ngProcess.OutputDataReceived += (sender, args) =>
                {
                    if (!string.IsNullOrEmpty(args.Data))
                    {
                        _logger.LogInformation("[Angular] {Data}", args.Data);
                    }
                };

                _ngProcess.ErrorDataReceived += (sender, args) =>
                {
                    if (!string.IsNullOrEmpty(args.Data))
                    {
                        _logger.LogWarning("[Angular] {Data}", args.Data);
                    }
                };

                _ngProcess.Start();
                _ngProcess.BeginOutputReadLine();
                _ngProcess.BeginErrorReadLine();

                _logger.LogInformation("Angular 開發伺服器進程已啟動，PID: {ProcessId}", _ngProcess.Id);

                // 就緒等待移到背景執行,不可在此 await:
                // IHostedService.StartAsync 會阻塞 host 啟動(Kestrel 要等它完成才開始監聽),
                // 而 HTTP 就緒探測要等 webpack 首次編譯完成,在此等待會讓 dotnet run 看似卡住。
                _ = Task.Run(() => WaitForReadyThenOpenBrowserAsync(openBrowser, _shutdownCts.Token));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "啟動 Angular 開發伺服器失敗");
                throw; // fail-fast:初次啟動失敗時讓 host 直接終止;重啟流程則由 OnConfigFileChanged 的 try/catch 接住
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("正在停止 Angular 開發伺服器...");
            _shutdownCts.Cancel(); // 終止背景就緒等待
            KillProcessTree();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            // 取消背景就緒等待與正在進行的重啟操作
            if (!_shutdownCts.IsCancellationRequested)
            {
                _shutdownCts.Cancel();
            }
            _shutdownCts.Dispose();
            _restartCts?.Cancel();

            // 停止檔案監控
            if (_configWatcher != null)
            {
                _configWatcher.EnableRaisingEvents = false;
                _configWatcher.Dispose();
            }

            // 停止並等待 debounce timer
            _debounceTimer?.Dispose();

            // 等待可能正在執行的重啟操作完成
            try
            {
                _restartLock.Wait(TimeSpan.FromSeconds(5));
            }
            catch (ObjectDisposedException)
            {
                // 如果已經被釋放，忽略
            }
            finally
            {
                _restartLock.Dispose();
            }

            _restartCts?.Dispose();
            KillProcessTree();
            _ngProcess?.Dispose();
        }

        private void KillProcessTree()
        {
            if (_ngProcess == null || _ngProcess.HasExited)
            {
                return;
            }

            try
            {
                var processId = _ngProcess.Id;
                _logger.LogInformation("正在終止進程樹，PID: {ProcessId}", processId);

                // 使用 taskkill /T /F 終止進程樹（包含子進程）
                var killProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = $"/T /F /PID {processId}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };

                killProcess.Start();
                killProcess.WaitForExit(5000);

                _logger.LogInformation("Angular 開發伺服器已終止");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "終止 Angular 開發伺服器時發生錯誤");
            }
        }

        /// <summary>
        /// 設定 webpack-exposes.config.js 檔案監控
        /// </summary>
        private void SetupConfigWatcher()
        {
            if (string.IsNullOrEmpty(_clientAppPath))
            {
                return;
            }

            const string configFile = "webpack-exposes.config.js";
            var configPath = Path.Combine(_clientAppPath, configFile);

            if (!File.Exists(configPath))
            {
                _logger.LogWarning("{File} 不存在，跳過檔案監控", configFile);
                return;
            }

            _configWatcher = new FileSystemWatcher
            {
                Path = _clientAppPath,
                Filter = configFile,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            _configWatcher.Changed += OnConfigFileChanged;
            _logger.LogInformation("已啟用 {File} 變更監控", configFile);
        }

        /// <summary>
        /// webpack-exposes.config.js 檔案變更處理（含防抖）
        /// </summary>
        private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
        {
            // 使用防抖機制，避免檔案儲存時觸發多次
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(async _ =>
            {
                // 如果已經 disposed，跳過
                if (_disposed)
                {
                    return;
                }

                // 嘗試取得鎖，若無法立即取得則跳過（避免重複重啟）
                if (!await _restartLock.WaitAsync(0))
                {
                    return;
                }

                try
                {
                    // 取消之前的重啟操作
                    _restartCts?.Cancel();
                    _restartCts?.Dispose();
                    _restartCts = new CancellationTokenSource();

                    _logger.LogWarning("========================================");
                    _logger.LogWarning("webpack-exposes.config.js 已變更！");
                    _logger.LogWarning("正在重啟 Angular 開發伺服器...");
                    _logger.LogWarning("========================================");

                    KillProcessTree();
                    await Task.Delay(1000, _restartCts.Token);
                    await StartServerAsync(_restartCts.Token, openBrowser: false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("重啟操作已取消");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "重啟 Angular 開發伺服器時發生錯誤");
                }
                finally
                {
                    _restartLock.Release();
                }
            }, null, 500, Timeout.Infinite);
        }

        /// <summary>
        /// 背景等待 ng serve 就緒,就緒後(首次啟動時)開啟瀏覽器。
        /// 獨立於 StartAsync 執行,host 不必等待 webpack 首次編譯完成即可開始監聽。
        /// </summary>
        private async Task WaitForReadyThenOpenBrowserAsync(bool openBrowser, CancellationToken cancellationToken)
        {
            try
            {
                var isReady = await WaitForServerReadyAsync(cancellationToken);
                if (isReady && openBrowser)
                {
                    OpenBrowser();
                }
            }
            catch (OperationCanceledException)
            {
                // host 關閉中,屬正常流程
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "背景等待 Angular 開發伺服器就緒時發生錯誤");
            }
        }

        private async Task<bool> WaitForServerReadyAsync(CancellationToken cancellationToken)
        {
            // HTTP 就緒探測要等 webpack 首次編譯完成,大型專案可能需要數分鐘;
            // 此等待已在背景執行、不阻塞 host,放寬上限即可
            var maxAttempts = 180;
            var attempt = 0;

            _logger.LogInformation("等待 Angular 開發伺服器就緒(等待首次編譯完成)...");

            while (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                if (await IsServerRespondingAsync(cancellationToken))
                {
                    _logger.LogInformation("Angular 開發伺服器已就緒，Port: {Port}", _port);
                    return true;
                }

                attempt++;
                await Task.Delay(1000, cancellationToken);
            }

            _logger.LogWarning("等待 Angular 開發伺服器逾時，但將繼續執行");
            return false;
        }

        /// <summary>
        /// 實際發 HTTP 請求確認 ng serve 已可回應。
        /// 只檢查 port 是否 open 並不可靠:ng serve 一啟動就 listen,但 webpack 首次編譯尚未完成,
        /// 此時請求會被 hang 住或看到「編譯中」。收到任何 HTTP 回應(含 404)才視為真正就緒。
        /// </summary>
        private async Task<bool> IsServerRespondingAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var response = await _readinessClient.GetAsync(
                    $"http://localhost:{_port}/",
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int GetPortFromProxyUrl()
        {
            var url = Environment.GetEnvironmentVariable("ANGULAR_SPA_PROXY_URL");
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                return uri.Port;
            }
            return 40001; // 預設值
        }

        private void OpenBrowser()
        {
            try
            {
                // 優先讀取 --urls CLI 參數（寫入 IConfiguration["urls"]），
                // 再 fallback 至 ASPNETCORE_URLS 環境變數
                var urls =
                    _configuration["urls"] ??
                    Environment.GetEnvironmentVariable("ASPNETCORE_URLS");

                if (string.IsNullOrEmpty(urls))
                {
                    _logger.LogInformation("未設定 urls 或 ASPNETCORE_URLS，跳過自動開啟瀏覽器");
                    return;
                }

                // 取得第一個 URL（可能有多個，用分號分隔）
                var url = urls.Split(';')[0].Trim();

                _logger.LogInformation("正在開啟瀏覽器: {Url}", url);

                // 使用系統預設瀏覽器開啟
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "無法自動開啟瀏覽器，請手動開啟");
            }
        }

    }
}

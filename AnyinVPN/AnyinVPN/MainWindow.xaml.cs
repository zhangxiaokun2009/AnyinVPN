using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.Web.WebView2.Core;
using System.Net;
using System.IO.Pipes;

namespace AnyinVPN
{
    public partial class MainWindow : Window
    {
        // 订阅地址
        private const string SUB_LINK = "https://example.com/jdsj05.txt";
        private const string AD_URL = "https://example.com/gg.html";
        private const string TZGG_URL = "https://example.com/tzgg.php";

        public ObservableCollection<ProxyNode> Nodes { get; set; } = new ObservableCollection<ProxyNode>();
        private Process _coreProc;

        // ===== 托盘控制变量 =====
        private bool _isExplicitExit = false;

        public MainWindow()
        {
            // ===== 强制单实例运行（后开的覆盖先开的） =====
            EnsureSingleInstanceOverride();

            InitializeComponent();
            this.DataContext = this;

            // 拦截窗口关闭，实现点击 X 隐藏到托盘
            this.Closing += MainWindow_Closing;

            if (DisconnectBtn != null) DisconnectBtn.IsEnabled = false;
            this.Loaded += async (s, e) => await InitializeApp();
        }

        // ==========================================
        //               单实例覆盖逻辑
        // ==========================================
        private void EnsureSingleInstanceOverride()
        {
            Process currentProcess = Process.GetCurrentProcess();
            Process[] processes = Process.GetProcessesByName(currentProcess.ProcessName);

            foreach (Process p in processes)
            {
                if (p.Id != currentProcess.Id)
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(1000);
                    }
                    catch { }
                }
            }
            StopCore();
        }

        // ==========================================
        //               托盘相关逻辑
        // ==========================================
        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (!_isExplicitExit)
            {
                e.Cancel = true;
                this.Hide();
            }
        }

        private void Show_Click(object sender, RoutedEventArgs e)
        {
            this.Show();
            this.Activate();
            this.WindowState = WindowState.Normal;
        }

        private void FullExit_Click(object sender, RoutedEventArgs e)
        {
            _isExplicitExit = true;
            StopCore();
            Application.Current.Shutdown();
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
        }

        // ==========================================
        //               核心业务逻辑
        // ==========================================

        public static class CryptoHelper
        {
            private static readonly byte[] Key = Encoding.UTF8.GetBytes("AnyinVPN_Secret_666_888_999_ABCC"); // 必须32位
            private static readonly byte[] IV = Encoding.UTF8.GetBytes("Anyin_Vector_123"); // 必须16位
            public static string Decrypt(string cipherText)
            {
                using var aes = System.Security.Cryptography.Aes.Create();
                using var decryptor = aes.CreateDecryptor(Key, IV);
                byte[] buffer = Convert.FromBase64String(cipherText);
                using var ms = new MemoryStream(buffer);
                using var cs = new System.Security.Cryptography.CryptoStream(ms, decryptor, System.Security.Cryptography.CryptoStreamMode.Read);
                using var sr = new StreamReader(cs);
                return sr.ReadToEnd();
            }
        }

        private async Task InitializeApp()
        {
            const string OFFICIAL_SITE = "https://example.com";

            if (!await CheckBasicInternetAccess())
            {
                StatusLabel.Text = "网络连接失败";
                MessageBox.Show("未检测到网络连接，请检查您的网络设置。", "安隐VPN");
                return;
            }

            try
            {
                // 1. 严格保持原始文字输出
                StatusLabel.Text = "正在同步云端服务...";

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                bool isResourceReady = false;

                // 2. 后台静默重试逻辑（最多5次）
                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    try
                    {
                        var subCheckTask = client.GetAsync(SUB_LINK, HttpCompletionOption.ResponseHeadersRead);
                        var adCheckTask = client.GetAsync(AD_URL, HttpCompletionOption.ResponseHeadersRead);
                        var tzCheckTask = client.GetAsync(TZGG_URL, HttpCompletionOption.ResponseHeadersRead);

                        var responses = await Task.WhenAll(subCheckTask, adCheckTask, tzCheckTask);

                        if (responses.All(r => r.IsSuccessStatusCode))
                        {
                            isResourceReady = true;
                            break; // 成功则跳出，不惊动用户
                        }
                    }
                    catch
                    {
                        // 静默处理异常，不输出任何错误信息
                    }

                    // 如果失败且没到5次，在后台等1秒再试
                    if (!isResourceReady && attempt < 5)
                    {
                        await Task.Delay(1000);
                    }
                }

                // 3. 如果5次都失败了，直接弹更新
                if (!isResourceReady)
                {
                    HandleVersionExpired(OFFICIAL_SITE);
                    return;
                }

                try
                {
                    await AdWebView.EnsureCoreWebView2Async();

                    // 基础设置
                    AdWebView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                    AdWebView.ZoomFactor = 1.0;
                    AdWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    AdWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

                    // 1. 生成带时间戳的地址，确保每次获取的都是最新的 gg.html
                    string antiCacheAdUrl = AD_URL + $"?t={DateTimeOffset.Now.ToUnixTimeMilliseconds()}";

                    // 2. 处理点击跳转：拦截所有非广告页面的请求
                    AdWebView.CoreWebView2.NavigationStarting += (s, e) =>
                    {
                        // 如果跳转的地址不是我们初始化的广告地址（且不是空白页），则在外部 Edge 打开
                        if (e.Uri != antiCacheAdUrl && e.Uri != "about:blank")
                        {
                            e.Cancel = true; // 拦截 WebView2 内部跳转

                            try
                            {
                                // 显式调用 Microsoft Edge 并在新标签页打开
                                Process.Start(new ProcessStartInfo
                                {
                                    FileName = "msedge.exe",
                                    Arguments = $"\"{e.Uri}\"",
                                    UseShellExecute = true
                                });
                            }
                            catch
                            {
                                // 兜底方案：如果 Edge 调用失败，则使用系统默认浏览器
                                Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
                            }
                        }
                    };

                    // 3. 处理 target="_blank" 的链接（某些 HTML 链接会触发新窗口请求）
                    AdWebView.CoreWebView2.NewWindowRequested += (s, e) =>
                    {
                        e.Handled = true; // 拦截新窗口弹出
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "msedge.exe",
                                Arguments = $"\"{e.Uri}\"",
                                UseShellExecute = true
                            });
                        }
                        catch { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); }
                    };

                    AdWebView.NavigationCompleted += async (s, e) =>
                    {
                        string script = @"
                        var style = document.createElement('style');
                        style.innerHTML = '* { margin: 0 !important; padding: 0 !important; overflow: hidden !important; } ' +
                                          'html, body { width: 100%; height: 100%; display: flex; justify-content: center; align-items: center; }';
                        document.head.appendChild(style);
        ";
                        await AdWebView.CoreWebView2.ExecuteScriptAsync(script);
                    };

                    // 加载带防缓存后缀的广告页
                    AdWebView.Source = new Uri(antiCacheAdUrl);
                }
                catch (Exception ex) { Debug.WriteLine("WebView2 Error: " + ex.Message); }

                var encryptedBase64 = await client.GetStringAsync(SUB_LINK);
                string raw = CryptoHelper.Decrypt(encryptedBase64.Trim());

                Nodes.Clear();
                var lines = raw.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var node = ProxyParser.Parse(line.Trim());
                    if (node != null) Nodes.Add(node);
                }

                if (Nodes.Count > 0)
                {
                    StatusLabel.Text = $"已获取 {Nodes.Count} 个节点，正在测速...";
                    await RunSpeedTestAndFilter();
                }
                else
                {
                    StatusLabel.Text = "暂无可用节点";
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "初始化异常，请重启软件";
                Debug.WriteLine("Error in InitializeApp: " + ex.Message);
            }
        }

        private async Task<bool> CheckBasicInternetAccess()
        {
            try
            {
                using var pingClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                await pingClient.GetAsync("https://www.baidu.com");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void HandleVersionExpired(string url)
        {
            Dispatcher.Invoke(() => {
                StatusLabel.Text = "此版本已失效";
                StatusLabel.Foreground = Brushes.Red;
                ConnectBtn.IsEnabled = false;

                var result = MessageBox.Show(
                    "服务器暂时无响应,可先尝试多重启两次\n\n或者此版本可能已失效，请前往官网下载正版客户端。\n\n官网地址：https://example.com\n\n是否立即跳转官网？",
                    "版本更新提示",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    }
                    catch { MessageBox.Show("无法自动打开浏览器，请手动访问: " + url); }
                }
            });
        }

        private async Task RunSpeedTestAndFilter()
        {
            var workingList = Nodes.ToList();
            var semaphore = new SemaphoreSlim(10);
            var tasks = workingList.Select(async node =>
            {
                await semaphore.WaitAsync();
                try
                {
                    int ms = await TestTcpLatency(node.Server, node.Port);
                    node.LatencyValue = ms;
                    node.Latency = ms >= 9999 ? "超时" : $"{ms}ms";
                }
                finally { semaphore.Release(); }
            }).ToList();

            await Task.WhenAll(tasks);

            Dispatcher.Invoke(() => {
                var successfulNodes = workingList.Where(n => n.LatencyValue < 9999)
                                                .OrderBy(n => n.LatencyValue).ToList();
                NodeListView.ItemsSource = null;
                Nodes.Clear();
                foreach (var n in successfulNodes) Nodes.Add(n);
                NodeListView.ItemsSource = Nodes;

                ICollectionView view = CollectionViewSource.GetDefaultView(Nodes);
                view?.Refresh();

                StatusLabel.Text = $"测速完成，{Nodes.Count} 个可用节点";
                if (Nodes.Count > 0) NodeListView.SelectedIndex = 0;
            });
        }

        private async Task<int> TestTcpLatency(string host, int port)
        {
            try
            {
                using var client = new TcpClient();
                Stopwatch sw = Stopwatch.StartNew();
                var connectTask = client.ConnectAsync(host, port);
                if (await Task.WhenAny(connectTask, Task.Delay(3000)) == connectTask && client.Connected)
                    return (int)sw.ElapsedMilliseconds;
            }
            catch { }
            return 9999;
        }

        private async Task<bool> ShowAdRequirementAsync()
        {
            bool isSuccess = false;
            var dialog = new Window
            {
                Title = "支持 安隐VPN",
                Width = 420,
                Height = 220,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                Background = Brushes.White,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false
            };

            var grid = new Grid { Margin = new Thickness(25) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var textBlock = new TextBlock
            {
                Text = "维持优质节点的成本高昂，\n请支持我们，点击观看一次广告即可免阻断连接。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(50, 50, 50))
            };
            Grid.SetRow(textBlock, 0);
            grid.Children.Add(textBlock);

            var stackPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 15, 0, 0)
            };
            Grid.SetRow(stackPanel, 1);

            var btnCancel = new Button
            {
                Content = "取消连接",
                Width = 110,
                Height = 35,
                Margin = new Thickness(0, 0, 30, 0),
                FontSize = 14,
                Background = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnCancel.Click += (s, e) => { dialog.Close(); };

            var btnAd = new Button
            {
                Content = "观看广告",
                Width = 110,
                Height = 35,
                FontSize = 14,
                Background = Brushes.DodgerBlue,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            btnAd.Click += async (s, e) =>
            {
                btnAd.IsEnabled = false;
                btnCancel.IsEnabled = false;
                int seconds = new Random().Next(10, 16);
                Process edgeProc = null;

                try
                {
                    string tempProfile = Path.Combine(Path.GetTempPath(), "AnyinVPN_AdProfile");

                    if (Directory.Exists(tempProfile))
                    {
                        try { Directory.Delete(tempProfile, true); } catch { }
                    }

                    string antiCacheUrl = TZGG_URL + $"?_t={DateTimeOffset.Now.ToUnixTimeMilliseconds()}";

                    edgeProc = new Process();
                    edgeProc.StartInfo.FileName = "msedge.exe";
                    edgeProc.StartInfo.Arguments = $"\"{antiCacheUrl}\" --user-data-dir=\"{tempProfile}\" --no-first-run --disk-cache-size=1 --disable-application-cache";
                    edgeProc.StartInfo.UseShellExecute = true;
                    edgeProc.Start();
                }
                catch
                {
                    MessageBox.Show(dialog, "无法拉起Edge浏览器，请确保系统已安装Microsoft Edge！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    btnAd.IsEnabled = true;
                    btnCancel.IsEnabled = true;
                    return;
                }

                bool isCompleted = true;
                for (int i = seconds; i > 0; i--)
                {
                    btnAd.Content = $"观看中 ({i}s)";
                    await Task.Delay(1000);
                    try
                    {
                        if (edgeProc.HasExited)
                        {
                            isCompleted = false;
                            break;
                        }
                    }
                    catch { }
                }

                if (!isCompleted)
                {
                    var result = MessageBox.Show(dialog, "倒计时未结束，网页已被关闭！\n请重试或取消连接。", "提示", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (result == MessageBoxResult.Yes)
                    {
                        btnAd.IsEnabled = true;
                        btnCancel.IsEnabled = true;
                        btnAd.Content = "观看广告";
                    }
                    else { dialog.Close(); }
                }
                else
                {
                    isSuccess = true;
                    dialog.Close();
                }
            };

            stackPanel.Children.Add(btnCancel);
            stackPanel.Children.Add(btnAd);
            grid.Children.Add(stackPanel);
            dialog.Content = grid;
            dialog.ShowDialog();
            return isSuccess;
        }

        // ==========================================
        //               连接逻辑
        // ==========================================

        private async void Connect_Click(object sender, RoutedEventArgs e)
        {
            var selected = NodeListView.SelectedItem as ProxyNode;
            if (selected == null)
            {
                MessageBox.Show("请先在列表中选择一个节点。", "安隐VPN");
                return;
            }

            StatusLabel.Text = "准备连接...";

            bool adCompleted = await ShowAdRequirementAsync();

            if (!adCompleted)
            {
                StatusLabel.Text = "连接已取消";
                return;
            }

            try
            {
                StopCore();
                await Task.Delay(200);

                StatusLabel.Text = "正在启动内核...";

                string coreDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "core");
                string exePath = Path.Combine(coreDir, "sing-box.exe");

                if (!File.Exists(exePath))
                {
                    MessageBox.Show("未找到核心组件 sing-box.exe，请检查 core 文件夹。", "错误");
                    return;
                }

                string configJson = SingBoxGenerator.Generate(selected);
                string pipeName = "anyin_conf_" + Guid.NewGuid().ToString("N") + ".json";
                string pipePath = @"\\.\pipe\" + pipeName;

                var pipeServer = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                        {
                            await pipeServer.WaitForConnectionAsync(cts.Token);
                            using (var sw = new StreamWriter(pipeServer, new UTF8Encoding(false)))
                            {
                                await sw.WriteAsync(configJson);
                                await sw.FlushAsync();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"命名管道传输失败: {ex.Message}");
                    }
                    finally
                    {
                        pipeServer.Dispose();
                    }
                });

                _coreProc = new Process();
                _coreProc.StartInfo.FileName = exePath;
                _coreProc.StartInfo.Arguments = $"run -c {pipePath}";
                _coreProc.StartInfo.WorkingDirectory = coreDir;
                _coreProc.StartInfo.UseShellExecute = false;
                _coreProc.StartInfo.CreateNoWindow = true;
                _coreProc.StartInfo.EnvironmentVariables["ENABLE_DEPRECATED_LEGACY_DNS_SERVERS"] = "true";

                if (_coreProc.Start())
                {
                    try
                    {
                        // --- 直接设置系统代理指向 sing-box 的混合监听端口 10809 ---
                        ProxyHelper.SetProxy(true, "127.0.0.1:10809");

                        StatusLabel.Text = $"已连接: {selected.Name}";
                        StatusLabel.Foreground = Brushes.DodgerBlue;
                        ConnectBtn.IsEnabled = false;
                        DisconnectBtn.IsEnabled = true;
                    }
                    catch (Exception ex)
                    {
                        StopCore();
                        MessageBox.Show("代理设置失败: " + ex.Message);
                    }
                }
                else
                {
                    throw new Exception("进程未能成功启动。");
                }
            }
            catch (Exception ex)
            {
                StopCore();
                MessageBox.Show($"连接失败！\n错误详情: {ex.Message}", "安隐VPN - 故障");
                ConnectBtn.IsEnabled = true;
                DisconnectBtn.IsEnabled = false;
                StatusLabel.Text = "连接异常";
            }
        }

        private void Disconnect_Click(object sender, RoutedEventArgs e)
        {
            StopCore();
            ConnectBtn.IsEnabled = true;
            DisconnectBtn.IsEnabled = false;
            StatusLabel.Text = "已断开连接";
            StatusLabel.Foreground = Brushes.Gray;
        }

        private void StopCore()
        {
            ProxyHelper.SetProxy(false);

            try
            {
                if (_coreProc != null && !_coreProc.HasExited) _coreProc.Kill();
                foreach (var p in Process.GetProcessesByName("sing-box")) p.Kill();
            }
            catch { }
        }

        private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }
    }

    public static class SingBoxGenerator
    {
        public static string Generate(ProxyNode node)
        {
            var p = node.ConfigParams;
            var outbound = new JObject { ["tag"] = "proxy", ["type"] = node.Protocol };

            outbound["server"] = node.Server;
            outbound["server_port"] = node.Port;

            if (node.Protocol == "vmess" || node.Protocol == "vless")
            {
                outbound["uuid"] = p["uuid"]?.ToString();
                if (node.Protocol == "vless" && p["flow"] != null)
                    outbound["flow"] = p["flow"].ToString();
            }
            else if (node.Protocol == "trojan")
            {
                outbound["password"] = p["password"]?.ToString();
            }

            if (node.Protocol == "vmess")
                outbound["alter_id"] = (p["alter_id"] != null) ? (int)p["alter_id"] : 0;

            string netType = p["transport"]?.ToString();
            if (!string.IsNullOrEmpty(netType) && netType != "tcp")
            {
                var transportObj = new JObject { ["type"] = netType };
                if (netType == "ws")
                {
                    if (p["path"] != null) transportObj["path"] = p["path"].ToString();
                    if (p["host"] != null) transportObj["headers"] = new JObject { ["Host"] = p["host"].ToString() };
                }
                else if (netType == "grpc" && p["serviceName"] != null)
                {
                    transportObj["service_name"] = p["serviceName"].ToString();
                }
                outbound["transport"] = transportObj;
            }

            string sec = p["security"]?.ToString();
            if (sec == "tls" || sec == "reality")
            {
                var tlsObj = new JObject { ["enabled"] = true };
                tlsObj["server_name"] = p["sni"]?.ToString() ?? p["host"]?.ToString() ?? node.Server;
                tlsObj["utls"] = new JObject { ["enabled"] = true, ["fingerprint"] = p["fp"]?.ToString() ?? "chrome" };

                if (sec == "reality")
                {
                    var realityObj = new JObject { ["enabled"] = true };
                    if (p["pbk"] != null) realityObj["public_key"] = p["pbk"].ToString();
                    if (p["sid"] != null) realityObj["short_id"] = p["sid"].ToString();
                    tlsObj["reality"] = realityObj;
                }
                outbound["tls"] = tlsObj;
            }

            var root = new JObject
            {
                ["log"] = new JObject { ["level"] = "info" },
                ["inbounds"] = new JArray(new JObject
                {
                    ["type"] = "mixed",
                    ["listen"] = "127.0.0.1",
                    ["listen_port"] = 10809
                }),
                ["outbounds"] = new JArray(outbound, new JObject { ["type"] = "direct", ["tag"] = "direct" }),
                ["route"] = new JObject
                {
                    ["rules"] = new JArray(
                        new JObject { ["domain"] = new JArray(node.Server), ["outbound"] = "direct" },
                        new JObject { ["ip_cidr"] = new JArray("127.0.0.0/8"), ["outbound"] = "direct" }
                    ),
                    ["final"] = "proxy"
                }
            };
            return root.ToString();
        }
    }

    public static class ProxyHelper
    {
        [DllImport("wininet.dll")]
        public static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        public static void SetProxy(bool enable, string server = "")
        {
            RegistryKey reg = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
            reg.SetValue("ProxyEnable", enable ? 1 : 0);
            if (enable) reg.SetValue("ProxyServer", server);
            reg.SetValue("ProxyOverride", "<local>");

            InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
        }
    }

    public static class ProxyParser
    {
        public static ProxyNode Parse(string url)
        {
            try
            {
                var node = new ProxyNode { RawUrl = url };
                node.Name = url.Contains("#") ? Uri.UnescapeDataString(url.Split('#').Last()) : "未知节点";
                string cleanUrl = url.Split('#')[0];

                if (url.StartsWith("vmess://"))
                {
                    node.Protocol = "vmess";
                    string base64 = cleanUrl.Substring(8);
                    string jsonStr = Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(base64)));
                    var v = JObject.Parse(jsonStr);

                    node.Server = v["add"]?.ToString();
                    node.Port = int.Parse(v["port"]?.ToString() ?? "0");
                    node.Name = v["ps"]?.ToString() ?? node.Name;

                    node.ConfigParams = new JObject
                    {
                        ["uuid"] = v["id"]?.ToString(),
                        ["alter_id"] = int.Parse(v["aid"]?.ToString() ?? "0"),
                        ["security"] = (v["tls"]?.ToString() == "tls") ? "tls" : "none",
                        ["transport"] = v["net"]?.ToString()?.ToLower() ?? "tcp",
                        ["host"] = v["host"]?.ToString(),
                        ["path"] = v["path"]?.ToString(),
                        ["sni"] = v["sni"]?.ToString() ?? v["host"]?.ToString(),
                        ["fp"] = "chrome"
                    };
                }
                else if (url.StartsWith("vless://") || url.StartsWith("trojan://"))
                {
                    var uri = new Uri(cleanUrl);
                    node.Protocol = url.StartsWith("vless://") ? "vless" : "trojan";
                    node.Server = uri.Host;
                    node.Port = uri.Port;

                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    node.ConfigParams = new JObject
                    {
                        ["uuid"] = uri.UserInfo,
                        ["password"] = uri.UserInfo,
                        ["transport"] = query["type"]?.ToLower() ?? "tcp",
                        ["security"] = query["security"]?.ToLower() ?? "none",
                        ["flow"] = query["flow"]?.ToLower(),
                        ["sni"] = query["sni"],
                        ["path"] = query["path"],
                        ["host"] = query["host"],
                        ["pbk"] = query["pbk"],
                        ["sid"] = query["sid"],
                        ["fp"] = query["fp"] ?? "chrome",
                        ["serviceName"] = query["serviceName"]
                    };
                }
                return node;
            }
            catch { return null; }
        }

        private static string PadBase64(string b)
        {
            string s = b.Replace("-", "+").Replace("_", "/");
            return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        }
    }

    public class ProxyNode : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string Protocol { get; set; }
        public string Server { get; set; }
        public int Port { get; set; }
        public string RawUrl { get; set; }
        public JObject ConfigParams { get; set; }
        private string _l = "等待中";
        public string Latency { get => _l; set { _l = value; OnPropertyChanged("Latency"); } }
        private int _lv = 9999;
        public int LatencyValue { get => _lv; set { _lv = value; OnPropertyChanged("LatencyValue"); } }
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
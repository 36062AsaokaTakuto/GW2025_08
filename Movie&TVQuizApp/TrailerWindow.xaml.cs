using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Movie_AnimeQuizApp {
    public partial class TrailerWindow : Window {
        private readonly string _watchUrl;

        // ===== Win32: モニター作業領域(タスクバー除外)へピクセルでフィット =====
        private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy,
            uint uFlags
        );

        private static readonly IntPtr HWND_TOP = new IntPtr(0);
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        // =========================================================

        public TrailerWindow(string watchUrl) {
            InitializeComponent();
            _watchUrl = watchUrl;

            Loaded += TrailerWindow_Loaded;

            // ★「表示後」に必ずフィット（外側でMaximized等されてもここで潰す）
            ContentRendered += (s, e) => {
                // 1回だけ確実に実行（表示後に）
                Dispatcher.BeginInvoke(new Action(() => FitToCurrentMonitorWorkAreaPx()));
            };

            // 解像度や画面構成が変わっても追従
            SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;

            Closed += (s, e) => {
                try { SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged; } catch { }
            };
        }

        private void SystemEvents_DisplaySettingsChanged(object sender, EventArgs e) {
            Dispatcher.BeginInvoke(new Action(() => FitToCurrentMonitorWorkAreaPx()));
        }

        private void FitToCurrentMonitorWorkAreaPx() {
            try {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                IntPtr hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (hMon == IntPtr.Zero) return;

                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (!GetMonitorInfo(hMon, ref mi)) return;

                int x = mi.rcWork.left;
                int y = mi.rcWork.top;
                int w = Math.Max(1, mi.rcWork.right - mi.rcWork.left);
                int h = Math.Max(1, mi.rcWork.bottom - mi.rcWork.top);

                // ★ここが重要：Maximized等を必ず潰して「作業領域ピッタリ」にする
                if (WindowState != WindowState.Normal) {
                    WindowState = WindowState.Normal;
                }

                // ピクセルで確実に合わせる（DPI換算ズレを回避）
                SetWindowPos(hwnd, HWND_TOP, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            catch {
                // 失敗しても落とさない
            }
        }

        private async void TrailerWindow_Loaded(object sender, RoutedEventArgs e) {
            // ロード直後にも念押し
            FitToCurrentMonitorWorkAreaPx();

            await TrailerWebView.EnsureCoreWebView2Async();

            TrailerWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            TrailerWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            TrailerWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;

            TrailerWebView.CoreWebView2.NavigationStarting += Core_NavigationStarting;

            // ★必ずwatchを開く（embed禁止）
            TrailerWebView.Source = new Uri(_watchUrl);
        }

        private void Core_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e) {
            if (string.IsNullOrEmpty(e.Uri)) return;

            if (IsAppScheme(e.Uri)) {
                e.Cancel = true;
                return;
            }

            if (e.Uri.IndexOf("youtube.com/embed/", StringComparison.OrdinalIgnoreCase) >= 0) {
                e.Cancel = true;

                string key = ExtractKeyFromEmbed(e.Uri);
                if (!string.IsNullOrEmpty(key)) {
                    TrailerWebView.Source = new Uri("https://www.youtube.com/watch?v=" + key + "&autoplay=1");
                } else {
                    TrailerWebView.Source = new Uri(_watchUrl);
                }
            }
        }

        private bool IsAppScheme(string uri) {
            if (uri.StartsWith("vnd.youtube:", StringComparison.OrdinalIgnoreCase)) return true;
            if (uri.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase)) return true;
            if (uri.StartsWith("intent:", StringComparison.OrdinalIgnoreCase)) return true;
            if (uri.StartsWith("ms-windows-store:", StringComparison.OrdinalIgnoreCase)) return true;
            if (uri.StartsWith("microsoft-store:", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private string ExtractKeyFromEmbed(string uri) {
            int idx = uri.IndexOf("/embed/", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "";

            string rest = uri.Substring(idx + "/embed/".Length);
            int q = rest.IndexOf("?", StringComparison.OrdinalIgnoreCase);
            return (q >= 0) ? rest.Substring(0, q) : rest;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Escape) Close();
        }
    }
}

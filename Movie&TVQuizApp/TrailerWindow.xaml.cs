using System;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace Movie_AnimeQuizApp {
    public partial class TrailerWindow : Window {
        private readonly string _watchUrl;

        public TrailerWindow(string watchUrl) {
            InitializeComponent();
            _watchUrl = watchUrl;

            Loaded += TrailerWindow_Loaded;
        }

        private async void TrailerWindow_Loaded(object sender, RoutedEventArgs e) {
            await TrailerWebView.EnsureCoreWebView2Async();

            TrailerWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            TrailerWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            TrailerWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;

            TrailerWebView.CoreWebView2.NavigationStarting += Core_NavigationStarting;

            // ★必ずwatchを開く（embed禁止＝153の原因を避ける）
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

                // embedに行こうとしたらwatchに戻す
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

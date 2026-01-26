using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace Movie_AnimeQuizApp {
    internal static class AppNav {
        private static DispatcherTimer _cleanupTimer;

        // Home処理中（= Main以外が前に出てきたら消す）
        public static bool ForceMain { get; private set; }

        public static void GoHome(Window from) {
            ForceMain = true;

            // MainWindow を取得 or 作成
            MainWindow main = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
            if (main == null) {
                main = new MainWindow();
                if (Application.Current.MainWindow == null) {
                    Application.Current.MainWindow = main;
                }
            }

            BringToFront(main);

            // 遅れて復活するウィンドウ対策：一定時間掃除を繰り返す
            StartCleanup(main);

            // 自分も閉じる（閉じられないなら隠す）
            try { from?.Close(); }
            catch { try { from?.Hide(); } catch { } }
        }

        private static void StartCleanup(MainWindow main) {
            if (_cleanupTimer == null) {
                _cleanupTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle);
                _cleanupTimer.Interval = TimeSpan.FromMilliseconds(120);
                _cleanupTimer.Tick += (_, __) => {
                    try {
                        var wins = Application.Current.Windows.Cast<Window>().ToList();

                        // Main以外を閉じる（復活しても次Tickでまた閉じる）
                        foreach (var w in wins) {
                            if (w == null) continue;
                            if (ReferenceEquals(w, main)) continue;

                            try { w.Close(); }
                            catch { try { w.Hide(); } catch { } }
                        }

                        BringToFront(main);

                        // Main以外が存在しなければ掃除終了
                        bool anyOther = Application.Current.Windows.Cast<Window>()
                            .Any(w => w != null && !ReferenceEquals(w, main));

                        if (!anyOther) {
                            _cleanupTimer.Stop();
                            ForceMain = false;
                        }
                    }
                    catch {
                        // 例外が出ても次Tickで再試行
                    }
                };
            }

            if (!_cleanupTimer.IsEnabled) _cleanupTimer.Start();
        }

        private static void BringToFront(Window w) {
            if (w == null) return;

            try {
                if (!w.IsVisible) w.Show();
                if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;

                w.WindowState = WindowState.Maximized;
                w.Activate();

                // 前面に来ない環境向け
                bool top = w.Topmost;
                w.Topmost = true;
                w.Topmost = false;
                w.Topmost = top;

                w.Focus();
            }
            catch { }
        }
    }
}

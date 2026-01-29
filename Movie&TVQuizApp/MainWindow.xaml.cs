// MainWindow.xaml.cs（クイズ検索候補：クリック後に勝手に再表示されない + 候補はクイズ検索ボックス直下に出す）
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Movie_AnimeQuizApp.Data;
using Movie_AnimeQuizApp.Data.Entities;
using Movie_AnimeQuizApp.Views;

namespace Movie_AnimeQuizApp {
    public partial class MainWindow : Window {

        private const string ApiKey = "0fa85086e0e7e8c979d1ff066b894bf5";
        private static readonly HttpClient _http = new HttpClient();

        private const string Placeholder = "作品名を検索...";
        private const string QuizPlaceholder = "クイズしたい作品名を入力";

        private bool _quizSearchPinned = false;

        // ★追加：候補クリックでTextを入れる時、TextChangedから候補再検索を発火させない
        private bool _suppressQuizSuggest = false;

        // ===== 作品検索候補（TMDB） =====
        public ObservableCollection<SuggestItem> Suggestions { get; } = new ObservableCollection<SuggestItem>();
        private CancellationTokenSource _ctsSuggest;

        private readonly DispatcherTimer _suggestDebounceTimer = new DispatcherTimer();
        private readonly SemaphoreSlim _suggestGate = new SemaphoreSlim(1, 1);
        private string _pendingSuggestQuery = "";
        private long _suggestSeq = 0;

        private readonly Dictionary<string, SuggestItem[]> _suggestCache = new Dictionary<string, SuggestItem[]>();
        private readonly LinkedList<string> _suggestCacheOrder = new LinkedList<string>();
        private const int SuggestCacheMax = 60;

        // ===== ★クイズ検索候補（DB）復活 =====
        public ObservableCollection<QuizSuggestItem> QuizSuggestions { get; } = new ObservableCollection<QuizSuggestItem>();
        private readonly DispatcherTimer _quizSuggestDebounceTimer = new DispatcherTimer();
        private CancellationTokenSource _ctsQuizSuggest;
        private string _pendingQuizSuggestQuery = "";
        private long _quizSuggestSeq = 0;

        // ===== メニュー非表示タイマー =====
        private readonly DispatcherTimer _menuHideTimer;

        // ===== 背景タイル =====
        private const double TileW = 150;
        private const double TileH = 225;
        private const double TileMargin = 2;
        private const double CornerRadius = 8;

        private const double LayerAngle = 14.0;
        private const double LayerScale = 1.35;
        private const double Safety = 2.10;

        private const string PosterSizePath = "/w185";
        private readonly SemaphoreSlim _dlGate = new SemaphoreSlim(8);

        private readonly Dictionary<string, BitmapSource> _imgCache = new Dictionary<string, BitmapSource>();
        private readonly object _cacheLock = new object();

        private readonly Dictionary<string, List<Image>> _waiters = new Dictionary<string, List<Image>>();
        private readonly HashSet<string> _downloading = new HashSet<string>();
        private readonly object _waitLock = new object();

        private readonly HashSet<string> _seenPosterUrls = new HashSet<string>();
        private readonly List<string> _posterUrls = new List<string>();

        private CancellationTokenSource _ctsLoad;
        private CancellationTokenSource _ctsResize;

        public MainWindow() {
            InitializeComponent();

            DataContext = this;

            ContentRendered += MainWindow_ContentRendered;
            SizeChanged += (_, __) => DebouncedRedraw();

            if (SearchTextBox != null && string.IsNullOrWhiteSpace(SearchTextBox.Text)) {
                SearchTextBox.Text = Placeholder;
                SearchTextBox.Foreground = Brushes.Gray;
            }

            if (QuizSearchTextBox != null) {
                QuizSearchTextBox.Text = "";
                QuizSearchTextBox.Foreground = Brushes.White;
                QuizSearchTextBox.IsReadOnlyCaretVisible = false;
            }

            UpdateQuizWatermark();

            _menuHideTimer = new DispatcherTimer();
            _menuHideTimer.Interval = TimeSpan.FromMilliseconds(180);
            _menuHideTimer.Tick += (_, __) => {
                _menuHideTimer.Stop();
                if (IsQuizPinnedOrEditing()) return;
                HideMenus();
            };

            _suggestDebounceTimer.Interval = TimeSpan.FromMilliseconds(520);
            _suggestDebounceTimer.Tick += async (_, __) => {
                _suggestDebounceTimer.Stop();
                string q = _pendingSuggestQuery;
                await RunSuggestAsync(q);
            };

            // ★クイズ候補（DB）用デバウンス
            _quizSuggestDebounceTimer.Interval = TimeSpan.FromMilliseconds(260);
            _quizSuggestDebounceTimer.Tick += async (_, __) => {
                _quizSuggestDebounceTimer.Stop();
                string q = _pendingQuizSuggestQuery;
                await RunQuizSuggestAsync(q);
            };
        }

        // 電源ボタン → 終了
        private void Power_Click(object sender, RoutedEventArgs e) {
            Application.Current.Shutdown();
        }

        private async void MainWindow_ContentRendered(object sender, EventArgs e) {
            ApplyCanvasTransform();
            await ReloadBackgroundAsync();
        }

        // =========================
        // クイズ回答（既存）
        // =========================
        private async void QuizSearchHit_Click(object sender, RoutedEventArgs e) {
            // ★追加：遷移前にクイズ候補のタイマー/通信を止める（戻った時に勝手に出ない）
            CancelQuizSuggestRequests();

            string title = (QuizSearchTextBox?.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(title)) return;

            await AppDb.InitAsync();

            Work work = await AppDb.Connection.Table<Work>()
                .Where(w => w.Title == title)
                .FirstOrDefaultAsync();

            if (work == null) return;

            var quizzes = await AppDb.Connection.Table<Quiz>()
                .Where(q => q.WorkKey == work.WorkKey)
                .ToListAsync();

            if (quizzes == null || quizzes.Count == 0) return;

            int firstQuizId = quizzes.OrderBy(q => q.QuizId).First().QuizId;
            Window quizWin = CreateQuizPlayWindowWindow(work.WorkKey, firstQuizId);
            if (quizWin == null) return;

            UnpinQuizSearch();
            HideMenus();
            HideSuggest();
            HideQuizSuggest();

            quizWin.Owner = this;

            this.Hide();
            quizWin.Closed += (_, __) => {
                try { this.Show(); this.Activate(); } catch { }
            };

            quizWin.WindowState = WindowState.Maximized;
            quizWin.Show();
        }

        private Window CreateQuizPlayWindowWindow(string workKey, int quizId) {
            try {
                var asm = Assembly.GetExecutingAssembly();
                var t =
                    asm.GetType("Movie_AnimeQuizApp.Views.QuizPlayWindow", false) ??
                    asm.GetType("Movie_AnimeQuizApp.QuizPlayWindow", false);

                if (t == null) return null;
                if (!typeof(Window).IsAssignableFrom(t)) return null;

                var ctor1 = t.GetConstructor(new Type[] { typeof(string) });
                if (ctor1 != null) return (Window)ctor1.Invoke(new object[] { workKey });

                var ctor2 = t.GetConstructor(new Type[] { typeof(string), typeof(int) });
                if (ctor2 != null) return (Window)ctor2.Invoke(new object[] { workKey, quizId });

                return null;
            }
            catch {
                return null;
            }
        }

        // =========================
        // ★候補クリック用：WorkKeyで直接クイズ開始（既存のまま）
        // =========================
        private async Task StartQuizByWorkKeyAsync(string workKey) {
            workKey = (workKey ?? "").Trim();
            if (string.IsNullOrWhiteSpace(workKey)) return;

            // ★追加：遷移前にクイズ候補のタイマー/通信を止める（戻った時に勝手に出ない）
            CancelQuizSuggestRequests();

            await AppDb.InitAsync();

            var quizzes = await AppDb.Connection.Table<Quiz>()
                .Where(q => q.WorkKey == workKey)
                .ToListAsync();

            if (quizzes == null || quizzes.Count == 0) return;

            int firstQuizId = quizzes.OrderBy(q => q.QuizId).First().QuizId;

            Window quizWin = CreateQuizPlayWindowWindow(workKey, firstQuizId);
            if (quizWin == null) return;

            UnpinQuizSearch();
            HideMenus();
            HideSuggest();
            HideQuizSuggest();

            quizWin.Owner = this;

            this.Hide();
            quizWin.Closed += (_, __) => {
                try { this.Show(); this.Activate(); } catch { }
            };

            quizWin.WindowState = WindowState.Maximized;
            quizWin.Show();
        }

        // =========================
        // ★クイズ候補：タイマー/通信を確実に停止（追加）
        // =========================
        private void CancelQuizSuggestRequests() {
            try { _quizSuggestDebounceTimer.Stop(); } catch { }
            _pendingQuizSuggestQuery = "";

            try {
                if (_ctsQuizSuggest != null) _ctsQuizSuggest.Cancel();
            } catch { }

            System.Threading.Interlocked.Increment(ref _quizSuggestSeq); // in-flight結果を無効化
            HideQuizSuggest();
        }

        // =========================
        // クリック外で候補/カーソル消す
        // =========================
        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e) {
            DependencyObject src = e.OriginalSource as DependencyObject;

            if (SearchTextBox != null && src != null && IsDescendant(src, SearchTextBox)) {
                UnpinQuizSearch();
                HideMenus();
                return;
            }

            if (SuggestBorder != null && SuggestBorder.Visibility == Visibility.Visible) {
                if (src != null) {
                    if (!IsDescendant(src, SuggestBorder) && (SearchTextBox == null || !IsDescendant(src, SearchTextBox))) {
                        HideSuggest();
                    }
                }
            }

            // ★クイズ候補（Popup）を閉じる（クリック先がクイズ検索欄 or 候補Popup内以外なら）
            if (QuizSuggestPopup != null && QuizSuggestPopup.IsOpen) {
                bool inQuizBox = (QuizSearchTextBox != null && src != null && IsDescendant(src, QuizSearchTextBox));
                bool inQuizPopup = (src != null && IsInQuizSuggestArea(src));

                if (!inQuizBox && !inQuizPopup) {
                    HideQuizSuggest();
                }
            }

            if (SearchTextBox != null && SearchTextBox.IsKeyboardFocusWithin) {
                if (src == null || !IsDescendant(src, SearchTextBox)) {
                    Keyboard.ClearFocus();
                }
            }

            // ★クイズ検索：候補Popup内クリックではフォーカスを消さない（クリックで候補選択できるように）
            if (QuizSearchTextBox != null && QuizSearchTextBox.IsKeyboardFocusWithin) {
                bool inQuizBox = (src != null && IsDescendant(src, QuizSearchTextBox));
                bool inQuizPopup = (src != null && IsInQuizSuggestArea(src));

                if (!inQuizBox && !inQuizPopup) {
                    Keyboard.ClearFocus();
                }
            }
        }

        private bool IsDescendant(DependencyObject child, DependencyObject ancestor) {
            var cur = child;
            while (cur != null) {
                if (cur == ancestor) return true;
                cur = VisualTreeHelper.GetParent(cur);
            }
            return false;
        }

        // ★クイズ候補Popup内か判定
        private bool IsInQuizSuggestArea(DependencyObject src) {
            if (src == null) return false;

            if (QuizSuggestList != null && IsDescendant(src, QuizSuggestList)) return true;

            if (QuizSuggestPopup != null) {
                var child = QuizSuggestPopup.Child as DependencyObject;
                if (child != null && IsDescendant(src, child)) return true;
            }

            return false;
        }

        // =========================
        // クイズ Watermark / ピン留め
        // =========================
        private void UpdateQuizWatermark() {
            if (QuizSearchWatermark == null || QuizSearchTextBox == null) return;
            QuizSearchWatermark.Visibility =
                string.IsNullOrWhiteSpace(QuizSearchTextBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PinQuizSearchIfNeeded() {
            if (_quizSearchPinned) return;
            if (QuizSearchTextBox == null) return;
            if (!string.IsNullOrWhiteSpace(QuizSearchTextBox.Text)) _quizSearchPinned = true;
        }

        private void UnpinQuizSearch() {
            _quizSearchPinned = false;
        }

        private bool IsQuizPinnedOrEditing() {
            if (_quizSearchPinned) return true;
            if (QuizSearchTextBox != null && QuizSearchTextBox.IsKeyboardFocusWithin) return true;
            return false;
        }

        // =========================
        // ヘッダー/メニュー（ホバー）
        // =========================
        private void MovieHeader_MouseEnter(object sender, MouseEventArgs e) {
            if (IsQuizPinnedOrEditing()) return;
            _menuHideTimer.Stop();
            if (MovieMenu != null) MovieMenu.Visibility = Visibility.Visible;
            if (TvMenu != null) TvMenu.Visibility = Visibility.Collapsed;
            if (QuizMenu != null) QuizMenu.Visibility = Visibility.Collapsed;
            HideQuizSearchPanel();
        }

        private void MovieHeader_MouseLeave(object sender, MouseEventArgs e) {
            _menuHideTimer.Stop();
            _menuHideTimer.Start();
        }

        private void TvHeader_MouseEnter(object sender, MouseEventArgs e) {
            if (IsQuizPinnedOrEditing()) return;
            _menuHideTimer.Stop();
            if (TvMenu != null) TvMenu.Visibility = Visibility.Visible;
            if (MovieMenu != null) MovieMenu.Visibility = Visibility.Collapsed;
            if (QuizMenu != null) QuizMenu.Visibility = Visibility.Collapsed;
            HideQuizSearchPanel();
        }

        private void TvHeader_MouseLeave(object sender, MouseEventArgs e) {
            _menuHideTimer.Stop();
            _menuHideTimer.Start();
        }

        private void QuizHeader_MouseEnter(object sender, MouseEventArgs e) {
            _menuHideTimer.Stop();
            if (QuizMenu != null) QuizMenu.Visibility = Visibility.Visible;
            if (MovieMenu != null) MovieMenu.Visibility = Visibility.Collapsed;
            if (TvMenu != null) TvMenu.Visibility = Visibility.Collapsed;
        }

        private void QuizHeader_MouseLeave(object sender, MouseEventArgs e) {
            _menuHideTimer.Stop();
            _menuHideTimer.Start();
        }

        private void Menu_MouseEnter(object sender, MouseEventArgs e) {
            _menuHideTimer.Stop();
        }

        private void Menu_MouseLeave(object sender, MouseEventArgs e) {
            _menuHideTimer.Stop();
            _menuHideTimer.Start();
        }

        private void HideMenus() {
            if (IsQuizPinnedOrEditing()) {
                if (MovieMenu != null) MovieMenu.Visibility = Visibility.Collapsed;
                if (TvMenu != null) TvMenu.Visibility = Visibility.Collapsed;

                if (QuizMenu != null) QuizMenu.Visibility = Visibility.Visible;
                if (QuizSearchPanel != null) QuizSearchPanel.Visibility = Visibility.Visible;

                UpdateQuizWatermark();
                return;
            }

            if (MovieMenu != null) MovieMenu.Visibility = Visibility.Collapsed;
            if (TvMenu != null) TvMenu.Visibility = Visibility.Collapsed;
            if (QuizMenu != null) QuizMenu.Visibility = Visibility.Collapsed;
            HideQuizSearchPanel();
        }

        private void CancelMenuHide() {
            _menuHideTimer.Stop();
        }

        private void ScheduleMenuHide() {
            if (IsQuizPinnedOrEditing()) return;
            _menuHideTimer.Stop();
            _menuHideTimer.Start();
        }

        // =========================
        // クイズ：検索パネル（ホバーで表示）
        // =========================
        private void QuizSearchHit_MouseEnter(object sender, MouseEventArgs e) {
            _menuHideTimer.Stop();
            ShowQuizSearchPanel();
        }

        private void QuizSearchHit_MouseLeave(object sender, MouseEventArgs e) {
            _menuHideTimer.Stop();
            _menuHideTimer.Start();
        }

        private void QuizSearchPanel_MouseEnter(object sender, MouseEventArgs e) {
            _menuHideTimer.Stop();
        }

        private void QuizSearchPanel_MouseLeave(object sender, MouseEventArgs e) {
            if (IsQuizPinnedOrEditing()) return;
            _menuHideTimer.Stop();
            _menuHideTimer.Start();
        }

        private void ShowQuizSearchPanel() {
            if (QuizSearchPanel != null) QuizSearchPanel.Visibility = Visibility.Visible;
            UpdateQuizWatermark();
        }

        private void HideQuizSearchPanel() {
            if (IsQuizPinnedOrEditing()) return;
            if (QuizSearchPanel != null) QuizSearchPanel.Visibility = Visibility.Collapsed;
        }

        private void QuizSearch_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
            if (QuizSearchTextBox == null) return;

            CancelMenuHide();
            if (QuizMenu != null) QuizMenu.Visibility = Visibility.Visible;
            if (QuizSearchPanel != null) QuizSearchPanel.Visibility = Visibility.Visible;

            if (!QuizSearchTextBox.IsKeyboardFocusWithin) {
                e.Handled = true;
                QuizSearchTextBox.Focus();
            }
        }

        private void QuizSearch_GotFocus(object sender, RoutedEventArgs e) {
            if (QuizSearchTextBox == null) return;

            CancelMenuHide();
            if (QuizMenu != null) QuizMenu.Visibility = Visibility.Visible;
            if (QuizSearchPanel != null) QuizSearchPanel.Visibility = Visibility.Visible;

            QuizSearchTextBox.Foreground = Brushes.White;
            QuizSearchTextBox.CaretBrush = Brushes.White;
            QuizSearchTextBox.IsReadOnlyCaretVisible = true;

            UpdateQuizWatermark();
        }

        private void QuizSearch_LostFocus(object sender, RoutedEventArgs e) {
            if (QuizSearchTextBox == null) return;

            QuizSearchTextBox.IsReadOnlyCaretVisible = false;
            UpdateQuizWatermark();

            // フォーカス移動先が候補Popup内なら閉じない（候補クリックを成立させる）
            var fe = Keyboard.FocusedElement as DependencyObject;
            if (fe == null || !IsInQuizSuggestArea(fe)) {
                HideQuizSuggest();
            }

            if (IsQuizPinnedOrEditing()) return;
            ScheduleMenuHide();
        }

        // ★クイズ検索 TextChanged：候補を出す
        private void QuizSearch_TextChanged(object sender, TextChangedEventArgs e) {
            if (QuizSearchTextBox == null) return;

            UpdateQuizWatermark();
            PinQuizSearchIfNeeded();

            // ★追加：候補クリック等のプログラムセット中は候補検索しない
            if (_suppressQuizSuggest) return;

            string q = (QuizSearchTextBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(q)) {
                HideQuizSuggest();
                return;
            }

            _pendingQuizSuggestQuery = q;
            _quizSuggestDebounceTimer.Stop();
            _quizSuggestDebounceTimer.Start();

            if (QuizSearchTextBox.IsKeyboardFocusWithin) {
                CancelMenuHide();
            }
        }

        // Esc は閉じる
        private void QuizSearch_PreviewKeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Escape) {
                UnpinQuizSearch();
                HideMenus();
                HideQuizSuggest();
                Keyboard.ClearFocus();
                e.Handled = true;
                return;
            }
        }

        // ★候補クリック → その作品でクイズ開始
        private async void QuizSuggestList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
            if (QuizSuggestList == null) return;
            var si = QuizSuggestList.SelectedItem as QuizSuggestItem;
            if (si == null) return;

            if (QuizSearchTextBox != null) {
                _suppressQuizSuggest = true;
                QuizSearchTextBox.Text = si.Title ?? "";
                _suppressQuizSuggest = false;
            }

            CancelQuizSuggestRequests();

            await StartQuizByWorkKeyAsync(si.WorkKey);
        }

        private async void QuizSuggestList_PreviewKeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Escape) {
                HideQuizSuggest();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter) {
                var si = (QuizSuggestList != null) ? (QuizSuggestList.SelectedItem as QuizSuggestItem) : null;
                if (si == null) return;

                if (QuizSearchTextBox != null) {
                    _suppressQuizSuggest = true;
                    QuizSearchTextBox.Text = si.Title ?? "";
                    _suppressQuizSuggest = false;
                }

                CancelQuizSuggestRequests();

                await StartQuizByWorkKeyAsync(si.WorkKey);

                e.Handled = true;
            }
        }

        private async Task RunQuizSuggestAsync(string query) {
            query = (query ?? "").Trim();
            if (query.Length == 0) { HideQuizSuggest(); return; }

            long mySeq = System.Threading.Interlocked.Increment(ref _quizSuggestSeq);

            if (_ctsQuizSuggest != null) _ctsQuizSuggest.Cancel();
            _ctsQuizSuggest = new CancellationTokenSource();
            var token = _ctsQuizSuggest.Token;

            try {
                await AppDb.InitAsync();
                if (token.IsCancellationRequested) return;
                if (mySeq != _quizSuggestSeq) return;

                string like = "%" + query + "%";

                var rows = await AppDb.Connection.QueryAsync<QuizSuggestRow>(
                    "SELECT w.WorkKey as WorkKey, w.Title as Title, w.PosterPath as PosterPath," +
                    " (SELECT COUNT(1) FROM Quiz q WHERE q.WorkKey = w.WorkKey) AS QuizCount" +
                    " FROM [Work] w" +
                    " WHERE w.Title LIKE ? AND EXISTS (SELECT 1 FROM Quiz q2 WHERE q2.WorkKey = w.WorkKey)" +
                    " ORDER BY w.Title COLLATE NOCASE" +
                    " LIMIT 12",
                    like
                );

                if (token.IsCancellationRequested) return;
                if (mySeq != _quizSuggestSeq) return;

                var list = new List<QuizSuggestItem>();
                if (rows != null) {
                    for (int i = 0; i < rows.Count; i++) {
                        var r = rows[i];
                        if (r == null) continue;
                        if (string.IsNullOrWhiteSpace(r.Title)) continue;

                        list.Add(new QuizSuggestItem {
                            WorkKey = r.WorkKey ?? "",
                            Title = r.Title ?? "",
                            PosterThumbUrl = BuildPosterThumbUrlFromStored(r.PosterPath),
                            Sub = "クイズ数: " + r.QuizCount.ToString()
                        });
                    }
                }

                await Dispatcher.InvokeAsync(() => {
                    if (mySeq != _quizSuggestSeq) return;

                    QuizSuggestions.Clear();
                    for (int i = 0; i < list.Count; i++) QuizSuggestions.Add(list[i]);

                    if (QuizSuggestions.Count > 0) ShowQuizSuggest();
                    else HideQuizSuggest();
                });
            }
            catch {
                await Dispatcher.InvokeAsync(() => HideQuizSuggest());
            }
        }

        // ★候補はクイズ検索ボックス直下に固定
        private void ShowQuizSuggest() {
            if (QuizSuggestPopup == null) return;

            if (QuizSearchTextBox != null) {
                QuizSuggestPopup.PlacementTarget = QuizSearchTextBox;
                QuizSuggestPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                QuizSuggestPopup.HorizontalOffset = -10;
                QuizSuggestPopup.VerticalOffset = 6;
            }

            QuizSuggestPopup.IsOpen = true;
        }

        private void HideQuizSuggest() {
            try { _quizSuggestDebounceTimer.Stop(); } catch { }
            _pendingQuizSuggestQuery = "";

            if (QuizSuggestPopup != null) QuizSuggestPopup.IsOpen = false;
            if (QuizSuggestList != null) QuizSuggestList.SelectedIndex = -1;
            QuizSuggestions.Clear();
        }

        private static string BuildPosterThumbUrlFromStored(string posterPathOrUrl) {
            string s = posterPathOrUrl ?? "";
            s = s.Trim();
            if (s.Length == 0) return "";

            if (s.StartsWith("http://") || s.StartsWith("https://")) {
                return s;
            }

            if (!s.StartsWith("/")) s = "/" + s;
            return "https://image.tmdb.org/t/p/w92" + s;
        }

        public class QuizSuggestItem {
            public string WorkKey { get; set; }
            public string Title { get; set; }
            public string PosterThumbUrl { get; set; }
            public string Sub { get; set; }
        }

        private class QuizSuggestRow {
            public string WorkKey { get; set; }
            public string Title { get; set; }
            public string PosterPath { get; set; }
            public int QuizCount { get; set; }
        }

        // =========================
        // 検索（作品検索：既存）
        // =========================
        private void SearchBox_GotFocus(object sender, RoutedEventArgs e) {
            if (SearchTextBox.Text == Placeholder) {
                SearchTextBox.Text = "";
                SearchTextBox.Foreground = Brushes.White;
            }
            SearchTextBox.CaretBrush = Brushes.White;
        }

        private void SearchBox_LostFocus(object sender, RoutedEventArgs e) {
            if (string.IsNullOrWhiteSpace(SearchTextBox.Text)) {
                SearchTextBox.Text = Placeholder;
                SearchTextBox.Foreground = Brushes.Gray;
                HideSuggest();
            }
        }

        private void Search_Click(object sender, RoutedEventArgs e) {
            string q = (SearchTextBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(q) || q == Placeholder) return;

            UnpinQuizSearch();
            HideMenus();
            HideSuggest();
            HideQuizSuggest();

            var win = new SearchResultWindow(q, ApiKey);
            win.Owner = this;

            this.Hide();
            win.Closed += (_, __) => {
                try { this.Show(); this.Activate(); } catch { }
            };

            win.WindowState = WindowState.Maximized;
            win.Show();
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) {
            if (SearchTextBox == null) return;

            string q = (SearchTextBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(q) || q == Placeholder) {
                HideSuggest();
                return;
            }

            if (q.Length < 2) {
                HideSuggest();
                return;
            }

            _pendingSuggestQuery = q;
            _suggestDebounceTimer.Stop();
            _suggestDebounceTimer.Start();
        }

        private async Task RunSuggestAsync(string query) {
            query = (query ?? "").Trim();
            if (query.Length < 2) { HideSuggest(); return; }

            long mySeq = System.Threading.Interlocked.Increment(ref _suggestSeq);

            string key = Normalize(query);

            SuggestItem[] cached;
            if (TryGetSuggestCache(key, out cached)) {
                await Dispatcher.InvokeAsync(() => {
                    if (mySeq != _suggestSeq) return;

                    Suggestions.Clear();
                    for (int i = 0; i < cached.Length; i++) Suggestions.Add(cached[i]);

                    if (Suggestions.Count > 0) ShowSuggest();
                    else HideSuggest();
                });
                return;
            }

            await _suggestGate.WaitAsync();
            try {
                if (mySeq != _suggestSeq) return;

                if (_ctsSuggest != null) _ctsSuggest.Cancel();
                _ctsSuggest = new CancellationTokenSource();
                var token = _ctsSuggest.Token;

                SuggestItem[] list = await FetchSuggestionsAsync(query, token);
                if (token.IsCancellationRequested) return;
                if (mySeq != _suggestSeq) return;

                PutSuggestCache(key, list);

                await Dispatcher.InvokeAsync(() => {
                    if (mySeq != _suggestSeq) return;

                    Suggestions.Clear();
                    for (int i = 0; i < list.Length; i++) Suggestions.Add(list[i]);

                    if (Suggestions.Count > 0) ShowSuggest();
                    else HideSuggest();
                });
            }
            catch {
            }
            finally {
                _suggestGate.Release();
            }
        }

        private bool TryGetSuggestCache(string key, out SuggestItem[] value) {
            if (_suggestCache.TryGetValue(key, out value)) {
                var node = _suggestCacheOrder.Find(key);
                if (node != null) {
                    _suggestCacheOrder.Remove(node);
                    _suggestCacheOrder.AddLast(node);
                }
                return true;
            }
            value = null;
            return false;
        }

        private void PutSuggestCache(string key, SuggestItem[] value) {
            if (key == null) return;

            if (_suggestCache.ContainsKey(key)) {
                _suggestCache[key] = value ?? new SuggestItem[0];

                var node = _suggestCacheOrder.Find(key);
                if (node != null) _suggestCacheOrder.Remove(node);
                _suggestCacheOrder.AddLast(key);
                return;
            }

            _suggestCache[key] = value ?? new SuggestItem[0];
            _suggestCacheOrder.AddLast(key);

            while (_suggestCacheOrder.Count > SuggestCacheMax) {
                string oldest = _suggestCacheOrder.First.Value;
                _suggestCacheOrder.RemoveFirst();
                _suggestCache.Remove(oldest);
            }
        }

        private void SearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Escape) {
                HideSuggest();
                return;
            }

            if (e.Key == Key.Down) {
                if (SuggestBorder != null && SuggestBorder.Visibility == Visibility.Visible && SuggestList != null) {
                    if (SuggestList.Items.Count > 0) {
                        int idx = SuggestList.SelectedIndex;
                        if (idx < 0) idx = 0;
                        else idx = Math.Min(idx + 1, SuggestList.Items.Count - 1);
                        SuggestList.SelectedIndex = idx;
                        SuggestList.ScrollIntoView(SuggestList.SelectedItem);
                        e.Handled = true;
                    }
                }
                return;
            }

            if (e.Key == Key.Up) {
                if (SuggestBorder != null && SuggestBorder.Visibility == Visibility.Visible && SuggestList != null) {
                    if (SuggestList.Items.Count > 0) {
                        int idx = SuggestList.SelectedIndex;
                        if (idx < 0) idx = 0;
                        else idx = Math.Max(idx - 1, 0);
                        SuggestList.SelectedIndex = idx;
                        SuggestList.ScrollIntoView(SuggestList.SelectedItem);
                        e.Handled = true;
                    }
                }
                return;
            }

            if (e.Key == Key.Enter) {
                if (SuggestBorder != null && SuggestBorder.Visibility == Visibility.Visible && SuggestList != null) {
                    var si = SuggestList.SelectedItem as SuggestItem;
                    if (si != null) {
                        OpenDetailFromSuggest(si);
                        e.Handled = true;
                        return;
                    }
                }

                Search_Click(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
        }

        private void SearchTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
            UnpinQuizSearch();
            HideMenus();
        }

        private void SuggestList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
            if (SuggestList == null) return;
            var si = SuggestList.SelectedItem as SuggestItem;
            if (si == null) return;
            OpenDetailFromSuggest(si);
        }

        private void OpenDetailFromSuggest(SuggestItem si) {
            UnpinQuizSearch();
            HideMenus();
            HideSuggest();
            HideQuizSuggest();

            var win = new SearchResultWindow(si.Id, si.MediaType, ApiKey);
            win.Owner = this;

            this.Hide();
            win.Closed += (_, __) => {
                try { this.Show(); this.Activate(); } catch { }
            };

            win.WindowState = WindowState.Maximized;
            win.Show();
        }

        private void ShowSuggest() {
            if (SuggestBorder != null) SuggestBorder.Visibility = Visibility.Visible;
        }

        private void HideSuggest() {
            if (SuggestBorder != null) SuggestBorder.Visibility = Visibility.Collapsed;
            if (SuggestList != null) SuggestList.SelectedIndex = -1;
            Suggestions.Clear();
        }

        private async Task<SuggestItem[]> FetchSuggestionsAsync(string query, CancellationToken token) {
            try {
                string url =
                    "https://api.themoviedb.org/3/search/multi?api_key=" + ApiKey +
                    "&language=ja-JP&include_adult=false&query=" + Uri.EscapeDataString(query) +
                    "&page=1";

                string json = await _http.GetStringAsync(url);
                if (token.IsCancellationRequested) return new SuggestItem[0];

                JObject obj = JObject.Parse(json);
                JArray results = obj["results"] as JArray;
                if (results == null || results.Count == 0) return new SuggestItem[0];

                string nq = Normalize(query);

                var items = new List<SuggestItem>();

                for (int i = 0; i < results.Count; i++) {
                    var r = results[i];
                    if (r == null) continue;

                    string mt = r["media_type"] != null ? r["media_type"].ToString() : "";
                    if (mt != "movie" && mt != "tv") continue;

                    int id = r["id"] != null ? r["id"].Value<int>() : 0;
                    if (id == 0) continue;

                    string title = GetTitle(r, mt);
                    if (string.IsNullOrWhiteSpace(title)) continue;

                    string poster = r["poster_path"] != null ? r["poster_path"].ToString() : "";
                    string dateRaw = GetDateRaw(r, mt);
                    string dateText = ToJaDate(dateRaw);

                    var si = new SuggestItem {
                        Id = id,
                        MediaType = mt,
                        Title = title,
                        Sub = (mt == "movie" ? "映画" : "テレビ番組") + (string.IsNullOrWhiteSpace(dateText) ? "" : " ・ " + dateText),
                        PosterThumbUrl = BuildPosterThumbUrl(poster),
                        NormTitle = Normalize(title)
                    };

                    items.Add(si);
                }

                var ordered = items
                    .Where(s => s.NormTitle.StartsWith(nq) || s.NormTitle.Contains(nq))
                    .OrderByDescending(s => s.NormTitle.StartsWith(nq))
                    .ThenBy(s => s.Title, StringComparer.CurrentCulture)
                    .Take(10)
                    .ToArray();

                return ordered;
            }
            catch {
                return new SuggestItem[0];
            }
        }

        private static string GetTitle(JToken r, string mediaType) {
            if (mediaType == "movie") {
                string t = r["title"] != null ? r["title"].ToString() : "";
                if (string.IsNullOrWhiteSpace(t) && r["original_title"] != null) t = r["original_title"].ToString();
                return t;
            }

            string n = r["name"] != null ? r["name"].ToString() : "";
            if (string.IsNullOrWhiteSpace(n) && r["original_name"] != null) n = r["original_name"].ToString();
            return n;
        }

        private static string GetDateRaw(JToken r, string mediaType) {
            if (mediaType == "movie") return r["release_date"] != null ? r["release_date"].ToString() : "";
            return r["first_air_date"] != null ? r["first_air_date"].ToString() : "";
        }

        private static string Normalize(string s) {
            if (s == null) return "";
            s = s.Trim().ToLowerInvariant();
            var chars = s.Where(ch => !char.IsWhiteSpace(ch)).ToArray();
            return new string(chars);
        }

        private static string ToJaDate(string raw) {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            DateTime dt;
            if (DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) {
                return dt.ToString("yyyy年M月d日", CultureInfo.GetCultureInfo("ja-JP"));
            }
            return raw;
        }

        private static string BuildPosterThumbUrl(string posterPath) {
            if (string.IsNullOrWhiteSpace(posterPath)) return "";
            if (!posterPath.StartsWith("/")) posterPath = "/" + posterPath;
            return "https://image.tmdb.org/t/p/w92" + posterPath;
        }

        public class SuggestItem {
            public int Id { get; set; }
            public string MediaType { get; set; }
            public string Title { get; set; }
            public string Sub { get; set; }
            public string PosterThumbUrl { get; set; }
            public string NormTitle { get; set; }
        }

        // =========================
        // 画面遷移/作成/削除（既存）
        // =========================
        private void QuizCreate_Click(object sender, RoutedEventArgs e) {
            HideMenus();
            HideSuggest();
            HideQuizSuggest();

            var w = new Movie_AnimeQuizApp.Views.QuizCreateWindow();
            w.Owner = this;

            this.Hide();
            try {
                w.WindowState = WindowState.Maximized;
                w.ShowDialog();
            }
            finally {
                try { this.Show(); this.Activate(); } catch { }
            }
        }

        private void QuizDelete_Click(object sender, RoutedEventArgs e) {
            HideMenus();
            HideSuggest();
            HideQuizSuggest();

            var w = new QuizDeleteWindow();
            w.Owner = this;
            w.ShowDialog();
        }

        private void MoviePopular_Click(object sender, RoutedEventArgs e) { OpenMediaBrowser(MediaBrowser.BrowseMode.MoviePopular); }
        private void MovieNowPlaying_Click(object sender, RoutedEventArgs e) { OpenMediaBrowser(MediaBrowser.BrowseMode.MovieNowPlaying); }
        private void TvPopular_Click(object sender, RoutedEventArgs e) { OpenMediaBrowser(MediaBrowser.BrowseMode.TvPopular); }
        private void TvOnAir_Click(object sender, RoutedEventArgs e) { OpenMediaBrowser(MediaBrowser.BrowseMode.TvOnAir); }

        private void OpenMediaBrowser(MediaBrowser.BrowseMode mode) {
            UnpinQuizSearch();
            HideMenus();
            HideSuggest();
            HideQuizSuggest();

            var win = new MediaBrowser(mode);
            win.Owner = this;

            this.Hide();
            win.Closed += (_, __) => {
                try { this.Show(); this.Activate(); } catch { }
            };

            win.WindowState = WindowState.Maximized;
            win.Show();
        }

        // =========================
        // 背景タイル（既存のまま）
        // =========================
        private void ApplyCanvasTransform() {
            double w = Math.Max(ActualWidth, 1000);
            double h = Math.Max(ActualHeight, 500);

            BackgroundCanvas.Width = w * Safety;
            BackgroundCanvas.Height = h * Safety;

            BackgroundCanvas.RenderTransformOrigin = new Point(0.5, 0.5);

            var tg = new TransformGroup();
            tg.Children.Add(new RotateTransform(LayerAngle));
            tg.Children.Add(new ScaleTransform(LayerScale, LayerScale));
            tg.Children.Add(new TranslateTransform(-BackgroundCanvas.Width * 0.18, -BackgroundCanvas.Height * 0.16));

            BackgroundCanvas.RenderTransform = tg;
        }

        private void DebouncedRedraw() {
            if (_ctsResize != null) _ctsResize.Cancel();
            _ctsResize = new CancellationTokenSource();
            var token = _ctsResize.Token;

            _ = Task.Run(async () => {
                try {
                    await Task.Delay(220, token);
                    if (token.IsCancellationRequested) return;

                    await Dispatcher.InvokeAsync(async () => {
                        ApplyCanvasTransform();
                        int need = CalcNeededTiles();
                        if (_posterUrls.Count < need) {
                            await EnsurePosterUrlsAsync(need, _ctsLoad != null ? _ctsLoad.Token : CancellationToken.None);
                        }
                        DrawTiles();
                        KickoffDownloads(_ctsLoad != null ? _ctsLoad.Token : CancellationToken.None);
                    });
                }
                catch { }
            });
        }

        private async Task ReloadBackgroundAsync() {
            if (_ctsLoad != null) _ctsLoad.Cancel();
            _ctsLoad = new CancellationTokenSource();
            var token = _ctsLoad.Token;

            lock (_waitLock) {
                _waiters.Clear();
                _downloading.Clear();
            }

            _seenPosterUrls.Clear();
            _posterUrls.Clear();

            BackgroundCanvas.Children.Clear();

            int needTiles = CalcNeededTiles();
            await EnsurePosterUrlsAsync(needTiles, token);

            DrawTiles();
            KickoffDownloads(token);
        }

        private int CalcNeededTiles() {
            double w = Math.Max(ActualWidth, 1000) * Safety;
            double h = Math.Max(ActualHeight, 500) * Safety;

            double stepX = TileW + TileMargin * 2;
            double stepY = TileH + TileMargin * 2;

            int cols = (int)Math.Ceiling((w / stepX) + 3);
            int rows = (int)Math.Ceiling((h / stepY) + 3);

            return cols * rows;
        }

        private async Task EnsurePosterUrlsAsync(int minUnique, CancellationToken token) {
            int guard = 0;
            int guardMax = 120;

            while (_posterUrls.Count < minUnique && guard < guardMax) {
                if (token.IsCancellationRequested) return;

                int before = _posterUrls.Count;

                await AddPosterUrlsFromUrlAsync("https://api.themoviedb.org/3/trending/movie/day?api_key=" + ApiKey + "&language=ja-JP");
                await AddPosterUrlsFromUrlAsync("https://api.themoviedb.org/3/trending/tv/day?api_key=" + ApiKey + "&language=ja-JP");
                await AddPosterUrlsFromUrlAsync("https://api.themoviedb.org/3/movie/popular?api_key=" + ApiKey + "&language=ja-JP&region=JP&page=" + (guard + 1));
                await AddPosterUrlsFromUrlAsync("https://api.themoviedb.org/3/tv/popular?api_key=" + ApiKey + "&language=ja-JP&page=" + (guard + 1));

                if (_posterUrls.Count == before) break;
                guard++;
            }
        }

        private async Task AddPosterUrlsFromUrlAsync(string url) {
            string json;
            try { json = await _http.GetStringAsync(url); }
            catch { return; }

            JObject data;
            try { data = JObject.Parse(json); }
            catch { return; }

            var results = data["results"] as JArray;
            if (results == null || results.Count == 0) return;

            for (int i = 0; i < results.Count; i++) {
                var r = results[i];
                if (r == null) continue;

                string posterPath = r["poster_path"] != null ? r["poster_path"].ToString() : null;
                if (string.IsNullOrWhiteSpace(posterPath)) continue;

                if (!posterPath.StartsWith("/")) posterPath = "/" + posterPath;

                string posterUrl = "https://image.tmdb.org/t/p" + PosterSizePath + posterPath;
                if (!_seenPosterUrls.Add(posterUrl)) continue;

                _posterUrls.Add(posterUrl);
            }
        }

        private void DrawTiles() {
            BackgroundCanvas.Children.Clear();
            if (_posterUrls.Count == 0) return;

            double stepX = TileW + TileMargin * 2;
            double stepY = TileH + TileMargin * 2;

            double startX = -stepX;
            double startY = -stepY;

            double w = BackgroundCanvas.Width;
            double h = BackgroundCanvas.Height;

            int cols = (int)Math.Ceiling((w / stepX) + 3);
            int rows = (int)Math.Ceiling((h / stepY) + 3);

            int need = cols * rows;
            int max = Math.Min(_posterUrls.Count, need);

            int idx = 0;
            for (int r = 0; r < rows; r++) {
                for (int c = 0; c < cols; c++) {
                    if (idx >= max) return;

                    double x = startX + (c * stepX);
                    double y = startY + (r * stepY);

                    string url = _posterUrls[idx];
                    var tile = CreateTile(url);

                    Canvas.SetLeft(tile, x);
                    Canvas.SetTop(tile, y);
                    BackgroundCanvas.Children.Add(tile);

                    idx++;
                }
            }
        }

        private UIElement CreateTile(string url) {
            var holder = new Border {
                Width = TileW,
                Height = TileH,
                CornerRadius = new CornerRadius(CornerRadius),
                ClipToBounds = true,
                Background = Brushes.Black,
                Margin = new Thickness(TileMargin)
            };

            var grid = new Grid();

            var img = new Image {
                Stretch = Stretch.UniformToFill,
                Opacity = 0.55
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.LowQuality);

            if (!TrySetFromCache(img, url))
                RegisterWaiter(url, img);

            grid.Children.Add(img);

            grid.Children.Add(new Rectangle {
                Fill = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)),
                IsHitTestVisible = false
            });

            holder.Child = grid;
            return holder;
        }

        private bool TrySetFromCache(Image img, string url) {
            BitmapSource cached = null;
            lock (_cacheLock) {
                if (_imgCache.ContainsKey(url))
                    cached = _imgCache[url];
            }

            if (cached != null) {
                img.Source = cached;
                return true;
            }
            return false;
        }

        private void RegisterWaiter(string url, Image img) {
            lock (_waitLock) {
                List<Image> list;
                if (!_waiters.TryGetValue(url, out list)) {
                    list = new List<Image>();
                    _waiters[url] = list;
                }
                list.Add(img);
            }

            KickoffDownloads(_ctsLoad != null ? _ctsLoad.Token : CancellationToken.None);
        }

        private void KickoffDownloads(CancellationToken token) {
            List<string> toDownload;

            lock (_waitLock) {
                toDownload = _waiters.Keys.Where(u => !_downloading.Contains(u)).ToList();
                for (int i = 0; i < toDownload.Count; i++) _downloading.Add(toDownload[i]);
            }

            for (int i = 0; i < toDownload.Count; i++)
                _ = DownloadAndApplyAsync(toDownload[i], token);
        }

        private async Task DownloadAndApplyAsync(string url, CancellationToken token) {
            try {
                BitmapSource cached = null;
                lock (_cacheLock) {
                    if (_imgCache.ContainsKey(url))
                        cached = _imgCache[url];
                }

                if (cached != null) {
                    await ApplyToWaitersAsync(url, cached, token);
                    return;
                }

                await _dlGate.WaitAsync(token);
                try {
                    byte[] bytes = await _http.GetByteArrayAsync(url);
                    if (token.IsCancellationRequested) return;

                    BitmapSource bmp = CreateFrozenBitmap(bytes, (int)(TileW * 2));

                    lock (_cacheLock) {
                        if (!_imgCache.ContainsKey(url))
                            _imgCache[url] = bmp;
                    }

                    await ApplyToWaitersAsync(url, bmp, token);
                }
                finally {
                    _dlGate.Release();
                }
            }
            catch {
            }
            finally {
                lock (_waitLock) {
                    _downloading.Remove(url);
                }
            }
        }

        private static BitmapSource CreateFrozenBitmap(byte[] bytes, int decodePixelWidth) {
            using (var ms = new MemoryStream(bytes)) {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = decodePixelWidth;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
        }

        private async Task ApplyToWaitersAsync(string url, BitmapSource bmp, CancellationToken token) {
            List<Image> targets = null;

            lock (_waitLock) {
                if (_waiters.ContainsKey(url)) {
                    targets = _waiters[url];
                    _waiters.Remove(url);
                }
            }

            if (targets == null || targets.Count == 0) return;
            if (token.IsCancellationRequested) return;

            await Dispatcher.InvokeAsync(() => {
                if (token.IsCancellationRequested) return;
                for (int i = 0; i < targets.Count; i++) targets[i].Source = bmp;
            });
        }
    }
}

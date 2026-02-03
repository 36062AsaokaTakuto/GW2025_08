// MainWindow.xaml.cs（ひらがな/カタカナ + 半角/全角 + 英字大小 対応 & クイズ候補を高速化）
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
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
using Movie_AnimeQuizApp.Share;

namespace Movie_AnimeQuizApp {
    public partial class MainWindow : Window {

        private const string ApiKey = "0fa85086e0e7e8c979d1ff066b894bf5";
        private static readonly HttpClient _http = new HttpClient();

        private const string Placeholder = "作品名を検索...";
        private const string QuizPlaceholder = "クイズしたい作品名を入力";

        private bool _quizSearchPinned = false;

        // ★候補クリックでTextを入れる時、TextChangedから候補再検索を発火させない
        private bool _suppressQuizSuggest = false;

        // ===== ★かな/幅/大小を無視して比較（ひらがな⇔カタカナ対応）=====
        private static readonly CompareInfo _jaComp =
            CultureInfo.GetCultureInfo("ja-JP").CompareInfo;

        private const CompareOptions _jaOpt =
            CompareOptions.IgnoreCase |
            CompareOptions.IgnoreKanaType |
            CompareOptions.IgnoreWidth;

        private static bool JaContains(string src, string q) {
            src = src ?? "";
            q = q ?? "";
            if (q.Length == 0) return true;
            return _jaComp.IndexOf(src, q, _jaOpt) >= 0;
        }

        private static bool JaStartsWith(string src, string q) {
            src = src ?? "";
            q = q ?? "";
            if (q.Length == 0) return true;
            return _jaComp.IsPrefix(src, q, _jaOpt);
        }

        // =====================================================
        // ★追加：ひらがな⇔カタカナの相互検索用ヘルパー（TMDB検索クエリ生成）
        // =====================================================
        private static string NormalizeNfkc(string s) =>
            (s ?? "").Normalize(NormalizationForm.FormKC);

        private static string ToHiragana(string s) {
            s = NormalizeNfkc(s);
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s) {
                if (ch >= '\u30A1' && ch <= '\u30F6') sb.Append((char)(ch - 0x60));
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        private static string ToKatakana(string s) {
            s = NormalizeNfkc(s);
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s) {
                if (ch >= '\u3041' && ch <= '\u3096') sb.Append((char)(ch + 0x60));
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        private static List<string> BuildQueryVariants(string query) {
            query = (query ?? "").Trim();
            if (query.Length == 0) return new List<string>();

            string nfkc = NormalizeNfkc(query);

            var list = new List<string>();
            void Add(string x) {
                x = (x ?? "").Trim();
                if (x.Length == 0) return;
                if (!list.Any(s => string.Equals(s, x, StringComparison.Ordinal))) list.Add(x);
            }

            // 優先順：そのまま → ひら → カタ
            Add(nfkc);
            Add(ToHiragana(nfkc));
            Add(ToKatakana(nfkc));

            return list;
        }

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

        // ===== ★クイズ検索候補（DB） =====
        public ObservableCollection<QuizSuggestItem> QuizSuggestions { get; } = new ObservableCollection<QuizSuggestItem>();
        private readonly DispatcherTimer _quizSuggestDebounceTimer = new DispatcherTimer();
        private CancellationTokenSource _ctsQuizSuggest;
        private string _pendingQuizSuggestQuery = "";
        private long _quizSuggestSeq = 0;

        // ★クイズ候補をDBから1回だけ読み込んでキャッシュ（高速化）
        private readonly SemaphoreSlim _quizIndexGate = new SemaphoreSlim(1, 1);
        private volatile bool _quizIndexLoaded = false;
        private List<QuizSuggestRow> _quizIndex = new List<QuizSuggestRow>();

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
            int imported = await QuizShare.ImportToDbAsync();
            if (imported > 0) InvalidateQuizIndex();
            ApplyCanvasTransform();
            await ReloadBackgroundAsync();
        }

        // =========================
        // ★追加：別画面へ行く前に「クイズ検索欄」を消す
        // =========================
        private void ClearQuizSearchTextForNavigation() {
            try { CancelQuizSuggestRequests(); } catch { }

            _quizSearchPinned = false;

            if (QuizSearchTextBox != null) {
                _suppressQuizSuggest = true;
                QuizSearchTextBox.Text = "";
                _suppressQuizSuggest = false;

                QuizSearchTextBox.IsReadOnlyCaretVisible = false;
            }

            UpdateQuizWatermark();
        }

        // =========================
        // ★追加：別画面へ行く前に「中央の作品名検索欄」を消す
        // =========================
        private void ClearMainSearchTextForNavigation() {
            try { CancelSuggestRequests(); } catch { }

            if (SearchTextBox == null) return;

            SearchTextBox.Text = Placeholder;
            SearchTextBox.Foreground = Brushes.Gray;
            SearchTextBox.CaretBrush = Brushes.White;

            if (SearchTextBox.IsKeyboardFocusWithin) {
                try { Keyboard.ClearFocus(); } catch { }
            }
        }

        // =========================
        // ★追加：中央検索（作品検索）の候補要求を確実に停止＆結果を無効化
        // =========================
        private void CancelSuggestRequests() {
            try { _suggestDebounceTimer.Stop(); } catch { }
            _pendingSuggestQuery = "";

            try {
                if (_ctsSuggest != null) _ctsSuggest.Cancel();
            }
            catch { }

            Interlocked.Increment(ref _suggestSeq);
            HideSuggest();
        }

        // =========================
        // クイズ回答
        // =========================
        private async void QuizSearchHit_Click(object sender, RoutedEventArgs e) {
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

            // ★遷移前に入力を消す
            ClearQuizSearchTextForNavigation();
            ClearMainSearchTextForNavigation();

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
        // ★候補クリック用：WorkKeyで直接クイズ開始
        // =========================
        private async Task StartQuizByWorkKeyAsync(string workKey) {
            workKey = (workKey ?? "").Trim();
            if (string.IsNullOrWhiteSpace(workKey)) return;

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

            // ★遷移前に入力を消す
            ClearQuizSearchTextForNavigation();
            ClearMainSearchTextForNavigation();

            quizWin.Owner = this;

            this.Hide();
            quizWin.Closed += (_, __) => {
                try { this.Show(); this.Activate(); } catch { }
            };

            quizWin.WindowState = WindowState.Maximized;
            quizWin.Show();
        }

        // =========================
        // ★クイズ候補：タイマー/通信を確実に停止
        // =========================
        private void CancelQuizSuggestRequests() {
            try { _quizSuggestDebounceTimer.Stop(); } catch { }
            _pendingQuizSuggestQuery = "";

            try {
                if (_ctsQuizSuggest != null) _ctsQuizSuggest.Cancel();
            }
            catch { }

            Interlocked.Increment(ref _quizSuggestSeq);
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

            // ★空欄クリック時だけ候補一覧を出す
            ShowQuizSuggestAllIfEmptyAndFocused();
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

            // ★空欄フォーカス時だけ候補一覧
            ShowQuizSuggestAllIfEmptyAndFocused();
        }

        private void QuizSearch_LostFocus(object sender, RoutedEventArgs e) {
            if (QuizSearchTextBox == null) return;

            QuizSearchTextBox.IsReadOnlyCaretVisible = false;
            UpdateQuizWatermark();

            var fe = Keyboard.FocusedElement as DependencyObject;
            if (fe == null || !IsInQuizSuggestArea(fe)) {
                HideQuizSuggest();
            }

            if (IsQuizPinnedOrEditing()) return;
            ScheduleMenuHide();
        }

        // ★クイズ検索 TextChanged：候補を出す（空欄なら“空欄候補”）
        private void QuizSearch_TextChanged(object sender, TextChangedEventArgs e) {
            if (QuizSearchTextBox == null) return;

            UpdateQuizWatermark();
            PinQuizSearchIfNeeded();

            if (_suppressQuizSuggest) return;

            string q = (QuizSearchTextBox.Text ?? "").Trim();

            if (string.IsNullOrWhiteSpace(q)) {
                if (QuizSearchTextBox.IsKeyboardFocusWithin) {
                    ShowQuizSuggestAllIfEmptyAndFocused();
                } else {
                    HideQuizSuggest();
                }
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

        // =========================
        // ★空欄クリック/空欄フォーカス時だけ「登録済み作品候補」を表示
        // =========================
        private void ShowQuizSuggestAllIfEmptyAndFocused() {
            if (QuizSearchTextBox == null) return;
            if (!QuizSearchTextBox.IsKeyboardFocusWithin) return;
            if (!string.IsNullOrWhiteSpace(QuizSearchTextBox.Text)) return;

            _ = RunQuizSuggestEmptyAsync();
        }

        // ★修正：登録済みを「全部」入れる（Take(12)なし）
        private async Task RunQuizSuggestEmptyAsync() {
            long mySeq = Interlocked.Increment(ref _quizSuggestSeq);

            try { _quizSuggestDebounceTimer.Stop(); } catch { }
            _pendingQuizSuggestQuery = "";

            if (_ctsQuizSuggest != null) _ctsQuizSuggest.Cancel();
            _ctsQuizSuggest = new CancellationTokenSource();
            var token = _ctsQuizSuggest.Token;

            try {
                await EnsureQuizIndexAsync(token);
                if (token.IsCancellationRequested) return;
                if (mySeq != _quizSuggestSeq) return;

                var src = _quizIndex;

                var list = await Task.Run(() => {
                    return src
                        .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Title))
                        .OrderBy(r => r.Title, StringComparer.CurrentCulture)
                        .Select(r => new QuizSuggestItem {
                            WorkKey = r.WorkKey ?? "",
                            Title = r.Title ?? "",
                            PosterThumbUrl = BuildPosterThumbUrlFromStored(r.PosterPath),
                            Sub = "クイズ数: " + r.QuizCount.ToString()
                        })
                        .ToList();
                }, token);

                if (token.IsCancellationRequested) return;
                if (mySeq != _quizSuggestSeq) return;

                await Dispatcher.InvokeAsync(() => {
                    if (mySeq != _quizSuggestSeq) return;

                    if (QuizSearchTextBox == null) return;
                    if (!QuizSearchTextBox.IsKeyboardFocusWithin) return;
                    if (!string.IsNullOrWhiteSpace(QuizSearchTextBox.Text)) return;

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

        // =========================
        // ★クイズ候補（DB）インデックス：最初に一度だけ読み込む
        // =========================
        private async Task EnsureQuizIndexAsync(CancellationToken token) {
            if (_quizIndexLoaded) return;

            await _quizIndexGate.WaitAsync(token);
            try {
                if (_quizIndexLoaded) return;

                await AppDb.InitAsync();
                if (token.IsCancellationRequested) return;

                var rows = await AppDb.Connection.QueryAsync<QuizSuggestRow>(
                    "SELECT w.WorkKey as WorkKey, w.Title as Title, w.PosterPath as PosterPath," +
                    " (SELECT COUNT(1) FROM Quiz q WHERE q.WorkKey = w.WorkKey) AS QuizCount" +
                    " FROM [Work] w" +
                    " WHERE EXISTS (SELECT 1 FROM Quiz q2 WHERE q2.WorkKey = w.WorkKey)" +
                    " ORDER BY w.Title COLLATE NOCASE"
                );

                _quizIndex = rows ?? new List<QuizSuggestRow>();
                _quizIndexLoaded = true;
            }
            catch {
                _quizIndex = new List<QuizSuggestRow>();
                _quizIndexLoaded = true;
            }
            finally {
                _quizIndexGate.Release();
            }
        }

        // ★クイズ作成/削除後に呼べば候補が更新される
        private void InvalidateQuizIndex() {
            _quizIndexLoaded = false;
            _quizIndex.Clear();
        }

        // =========================
        // ★クイズ候補検索（高速・かな対応）
        // =========================
        private async Task RunQuizSuggestAsync(string query) {
            query = (query ?? "").Trim();
            if (query.Length == 0) { HideQuizSuggest(); return; }

            long mySeq = Interlocked.Increment(ref _quizSuggestSeq);

            if (_ctsQuizSuggest != null) _ctsQuizSuggest.Cancel();
            _ctsQuizSuggest = new CancellationTokenSource();
            var token = _ctsQuizSuggest.Token;

            try {
                await EnsureQuizIndexAsync(token);
                if (token.IsCancellationRequested) return;
                if (mySeq != _quizSuggestSeq) return;

                var src = _quizIndex;

                var list = await Task.Run(() => {
                    var ret = new List<QuizSuggestItem>();

                    for (int i = 0; i < src.Count; i++) {
                        if (token.IsCancellationRequested) break;

                        var r = src[i];
                        if (r == null) continue;

                        string title = r.Title ?? "";
                        if (title.Length == 0) continue;

                        if (!JaContains(title, query)) continue;

                        ret.Add(new QuizSuggestItem {
                            WorkKey = r.WorkKey ?? "",
                            Title = title,
                            PosterThumbUrl = BuildPosterThumbUrlFromStored(r.PosterPath),
                            Sub = "クイズ数: " + r.QuizCount.ToString()
                        });
                    }

                    return ret
                        .OrderByDescending(x => JaStartsWith(x.Title, query))
                        .ThenBy(x => x.Title, StringComparer.CurrentCulture)
                        .Take(12)
                        .ToList();
                }, token);

                if (token.IsCancellationRequested) return;
                if (mySeq != _quizSuggestSeq) return;

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

                // ★非同期の戻りで再表示されないようにキャンセル＋非表示
                CancelSuggestRequests();
            }
        }

        private void Search_Click(object sender, RoutedEventArgs e) {
            string q = (SearchTextBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(q) || q == Placeholder) return;

            UnpinQuizSearch();
            HideMenus();
            HideSuggest();
            HideQuizSuggest();

            // ★遷移前に入力を消す
            ClearQuizSearchTextForNavigation();
            ClearMainSearchTextForNavigation();

            var win = new SearchResultWindow(q, ApiKey);
            win.Owner = this;

            this.Hide();
            win.Closed += (_, __) => {
                try { this.Show(); this.Activate(); } catch { }
            };

            win.WindowState = WindowState.Maximized;
            win.Show();
        }

        // ★修正：文字を消したら候補も必ず消える（in-flight を止める）
        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) {
            if (SearchTextBox == null) return;

            string q = (SearchTextBox.Text ?? "").Trim();

            if (string.IsNullOrWhiteSpace(q) || q == Placeholder || q.Length < 2) {
                CancelSuggestRequests();
                return;
            }

            _pendingSuggestQuery = q;
            _suggestDebounceTimer.Stop();
            _suggestDebounceTimer.Start();
        }

        private async Task RunSuggestAsync(string query) {
            query = (query ?? "").Trim();
            if (query.Length < 2) { HideSuggest(); return; }

            long mySeq = Interlocked.Increment(ref _suggestSeq);

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

            // ★ここが修正点：Enterを押しても画面遷移しない（検索も候補決定も実行しない）
            if (e.Key == Key.Enter) {
                e.Handled = true; // ピロン音も防ぐ
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

            // ★遷移前に入力を消す
            ClearQuizSearchTextForNavigation();
            ClearMainSearchTextForNavigation();

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

        // =====================================================
        // ★TMDB 1回分の検索（バリアント検索の部品）
        // =====================================================
        private async Task<List<SuggestItem>> FetchSuggestionsOnceAsync(string query, CancellationToken token) {
            try {
                string url =
                    "https://api.themoviedb.org/3/search/multi?api_key=" + ApiKey +
                    "&language=ja-JP&include_adult=false&query=" + Uri.EscapeDataString(query) +
                    "&page=1";

                string json = await _http.GetStringAsync(url);
                if (token.IsCancellationRequested) return new List<SuggestItem>();

                JObject obj = JObject.Parse(json);
                JArray results = obj["results"] as JArray;
                if (results == null || results.Count == 0) return new List<SuggestItem>();

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

                    items.Add(new SuggestItem {
                        Id = id,
                        MediaType = mt,
                        Title = title,
                        Sub = (mt == "movie" ? "映画" : "テレビ番組") + (string.IsNullOrWhiteSpace(dateText) ? "" : " ・ " + dateText),
                        PosterThumbUrl = BuildPosterThumbUrl(poster),
                        NormTitle = Normalize(title)
                    });
                }

                return items;
            }
            catch {
                return new List<SuggestItem>();
            }
        }

        // =====================================================
        // ★中央検索（TMDB候補）を「そのまま/ひら/カタ」で検索→マージ
        // =====================================================
        private async Task<SuggestItem[]> FetchSuggestionsAsync(string query, CancellationToken token) {
            try {
                var variants = BuildQueryVariants(query);
                if (variants.Count == 0) return new SuggestItem[0];

                // ★(mediaType:id)で重複排除してマージ
                var map = new Dictionary<string, SuggestItem>();

                for (int i = 0; i < variants.Count; i++) {
                    if (token.IsCancellationRequested) break;

                    var part = await FetchSuggestionsOnceAsync(variants[i], token);
                    if (token.IsCancellationRequested) break;

                    for (int j = 0; j < part.Count; j++) {
                        var s = part[j];
                        string key = s.MediaType + ":" + s.Id.ToString();
                        if (!map.ContainsKey(key)) map[key] = s;
                    }
                }

                var merged = map.Values.ToList();

                // ★最後の絞り込みは「元の query」で（IgnoreKanaTypeなのでひら/カタ差OK）
                var ordered = merged
                    .Where(s => JaContains(s.Title, query))
                    .OrderByDescending(s => JaStartsWith(s.Title, query))
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

        // ★変更：キャッシュキーや比較用の正規化（NFKC + ひらがな寄せ + 英字小文字 + 空白除去）
        private static string Normalize(string s) {
            s = NormalizeNfkc(s ?? "");
            s = ToHiragana(s);
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
        // 画面遷移/作成/削除
        // =========================
        private void QuizCreate_Click(object sender, RoutedEventArgs e) {
            HideMenus();
            HideSuggest();
            HideQuizSuggest();

            // ★遷移前に入力を消す
            ClearQuizSearchTextForNavigation();
            ClearMainSearchTextForNavigation();

            var w = new Movie_AnimeQuizApp.Views.QuizCreateWindow();
            w.Owner = this;

            this.Hide();
            try {
                w.WindowState = WindowState.Maximized;
                w.ShowDialog();
            }
            finally {
                InvalidateQuizIndex();
                try { this.Show(); this.Activate(); } catch { }
            }
        }

        private void QuizDelete_Click(object sender, RoutedEventArgs e) {
            HideMenus();
            HideSuggest();
            HideQuizSuggest();

            // ★遷移前に入力を消す
            ClearQuizSearchTextForNavigation();
            ClearMainSearchTextForNavigation();

            var w = new QuizDeleteWindow();
            w.Owner = this;
            w.ShowDialog();

            InvalidateQuizIndex();
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

            // ★遷移前に入力を消す
            ClearQuizSearchTextForNavigation();
            ClearMainSearchTextForNavigation();

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
        // 背景タイル
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
                int page = guard + 1;

                // 1) 日本のアニメ（TV：Animation=16 / 日本語）
                await AddPosterUrlsFromUrlAsync(
                    "https://api.themoviedb.org/3/discover/tv?api_key=" + ApiKey +
                    "&language=ja-JP&sort_by=popularity.desc" +
                    "&with_genres=16&with_original_language=ja" +
                    "&with_without_genres=27" +
                    "&page=" + page
                );

                // 2) 日本で人気の映画（region=JP）
                await AddPosterUrlsFromUrlAsync(
                    "https://api.themoviedb.org/3/discover/movie?api_key=" + ApiKey +
                    "&language=ja-JP&region=JP&sort_by=popularity.desc" +
                    "&include_adult=false" +
                    "&with_without_genres=27" +
                    "&page=" + page
                );

                // 3) 人気のドラマ（TV：Drama=18）
                await AddPosterUrlsFromUrlAsync(
                    "https://api.themoviedb.org/3/discover/tv?api_key=" + ApiKey +
                    "&language=ja-JP&sort_by=popularity.desc" +
                    "&with_genres=18" +
                    "&with_without_genres=27" +
                    "&page=" + page
                );

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

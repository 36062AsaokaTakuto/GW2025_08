using Movie_AnimeQuizApp.Data;
using Movie_AnimeQuizApp.Data.Entities;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Movie_AnimeQuizApp {
    public partial class MediaBrowser : Window {
        private const string ApiKey = "0fa85086e0e7e8c979d1ff066b894bf5";
        private static readonly HttpClient _http = new HttpClient();

        private const int Columns = 8;
        private const int RowsPerChunk = 5;
        private const int ChunkSize = Columns * RowsPerChunk; // 40
        private const string PosterSize = "/w342";

        private readonly DispatcherTimer _menuHideTimer = new DispatcherTimer();
        private CancellationTokenSource _cts;

        private readonly Dictionary<string, BitmapSource> _imgCache = new Dictionary<string, BitmapSource>();
        private readonly object _imgLock = new object();
        private readonly SemaphoreSlim _imgGate = new SemaphoreSlim(10);

        private readonly HashSet<string> _seenKeys = new HashSet<string>();

        private int _apiPage = 1;
        private int _apiTotalPages = int.MaxValue;

        private readonly List<MediaItem> _buffer = new List<MediaItem>();
        private int _bufferIndex = 0;

        public ObservableCollection<BrowserRow> Items { get; private set; } = new ObservableCollection<BrowserRow>();
        public ObservableCollection<GenreOption> Genres { get; private set; } = new ObservableCollection<GenreOption>();

        // ★8列相当の幅（WrapPanel.ItemWidthに使う）
        public double CardWidth {
            get { return (double)GetValue(CardWidthProperty); }
            set { SetValue(CardWidthProperty, value); }
        }
        public static readonly DependencyProperty CardWidthProperty =
            DependencyProperty.Register("CardWidth", typeof(double), typeof(MediaBrowser),
                new PropertyMetadata(200.0));

        // =====================================================
        // ★かな/幅/大小を無視して比較（MainWindowと同じ）
        // =====================================================
        private static readonly CompareInfo _jaComp = CultureInfo.GetCultureInfo("ja-JP").CompareInfo;
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

        // =========================================================
        // ★クイズ候補（DBにある「クイズが存在する作品」）: インデックスを1回だけ読み込み
        // =========================================================
        private readonly ObservableCollection<QuizWorkSuggestItem> _quizWorkSuggestions
            = new ObservableCollection<QuizWorkSuggestItem>();

        private readonly SemaphoreSlim _quizIndexGate = new SemaphoreSlim(1, 1);
        private volatile bool _quizIndexLoaded = false;
        private List<QuizSuggestRow> _quizIndex = new List<QuizSuggestRow>();

        private readonly DispatcherTimer _quizSuggestDebounceTimer = new DispatcherTimer();
        private CancellationTokenSource _ctsQuizSuggest;
        private string _pendingQuizSuggestQuery = "";
        private long _quizSuggestSeq = 0;

        // ★候補クリックでTextを入れる時、TextChangedから候補再検索を発火させない
        private bool _suppressQuizSuggest = false;

        // ★候補表示の「重い」「出ない」「一番下へ復元」を防ぐ（SearchResultWindowと同じ）
        private int _suggestShowSeq = 0;
        private ScrollViewer _suggestScrollViewer = null;
        private string _lastSuggestQueryText = null;

        public enum BrowseMode {
            MoviePopular,
            MovieNowPlaying,
            TvPopular,
            TvOnAir
        }

        private BrowseMode _mode;
        private int _genreId = -1;

        public MediaBrowser() : this(BrowseMode.MoviePopular) { }

        public MediaBrowser(BrowseMode mode) {
            InitializeComponent();

            _mode = mode;

            // ★Bindingの要
            DataContext = this;

            // ★クイズ候補ListBoxのItemsSource
            if (QuizWorkSuggestList != null) {
                QuizWorkSuggestList.ItemsSource = _quizWorkSuggestions;

                // ★「3件くらいしか見えない」対策：MainWindowに近い表示量（約10件分見える高さ）
                // （XAMLは触らず、コードで設定）
                QuizWorkSuggestList.MaxHeight = 900;

                // ★候補クリックを確実に拾う（handledでも拾う）
                QuizWorkSuggestList.AddHandler(
                    UIElement.PreviewMouseLeftButtonUpEvent,
                    new MouseButtonEventHandler(QuizWorkSuggestList_PreviewMouseLeftButtonUp),
                    true
                );
            }

            Genres.Clear();
            Genres.Add(new GenreOption { Id = -1, Name = "すべて", IsSelected = true });

            Loaded += MediaBrowser_Loaded;
            SizeChanged += (_, __) => UpdateCardWidth();
            Activated += MediaBrowser_Activated;

            Closing += (_, __) => {
                try { _cts?.Cancel(); } catch { }
                try { _ctsQuizSuggest?.Cancel(); } catch { }
                try { _imgGate.Dispose(); } catch { }
            };

            _menuHideTimer.Interval = TimeSpan.FromMilliseconds(180);
            _menuHideTimer.Tick += (_, __) => {
                _menuHideTimer.Stop();
                HideMenus();
            };

            // ★クイズ候補（デバウンス）
            _quizSuggestDebounceTimer.Interval = TimeSpan.FromMilliseconds(260);
            _quizSuggestDebounceTimer.Tick += async (_, __) => {
                _quizSuggestDebounceTimer.Stop();
                string qtext = _pendingQuizSuggestQuery;
                await RunQuizWorkSuggestAsync(qtext);
            };

            InitQuizHeader();
        }

        // Home中に復活したら即消す
        private void MediaBrowser_Activated(object sender, EventArgs e) {
            if (AppNav.ForceMain) {
                try { Close(); } catch { try { Hide(); } catch { } }
            }
        }

        private void InitQuizHeader() {
            // クイズ検索：初期は空（プレースホルダーTextBlock表示前提）
            if (QuizSearchTextBox != null) {
                QuizSearchTextBox.Text = "";
                QuizSearchTextBox.Foreground = Brushes.White;
                QuizSearchTextBox.IsReadOnlyCaretVisible = false;
            }
            HideQuizWorkSuggest();
        }

        private async void MediaBrowser_Loaded(object sender, RoutedEventArgs e) {
            UpdateCardWidth();
            ApplyModeTitle();
            try { await LoadGenresAsync(); } catch { }
            await ResetAndLoadAsync();
        }

        private void UpdateCardWidth() {
            double w = ActualWidth;
            if (double.IsNaN(w) || w <= 0) return;

            double usable = Math.Max(640, w - 48);
            double cw = usable / Columns;
            if (cw < 120) cw = 120;

            CardWidth = cw;
        }

        // =========================================================
        // ★カードクリック：ID指定で確実に同じ作品へ
        // =========================================================
        private void MediaCard_Click(object sender, RoutedEventArgs e) {
            ClearQuizSearchUi();

            FrameworkElement fe = sender as FrameworkElement;
            BrowserRow row = fe != null ? fe.DataContext as BrowserRow : null;
            if (row == null || row.IsLoadMore || row.Media == null) return;

            SearchResultWindow detail = new SearchResultWindow(row.Media.Id, row.Media.MediaType, ApiKey);
            detail.Owner = this;

            this.Hide();
            detail.Closed += (_, __) => {
                if (AppNav.ForceMain) return;
                try { this.Show(); this.Activate(); } catch { }
            };

            detail.WindowState = WindowState.Maximized;
            detail.Show();
        }

        // =========================================================
        // Home（確実にMainだけにする）
        // =========================================================
        private void Home_Click(object sender, RoutedEventArgs e) {
            ClearQuizSearchUi();
            AppNav.GoHome(this);
        }

        // =========================================================
        // 外クリック：クイズ検索のフォーカス制御（MainWindowと同じ判定）
        // =========================================================
        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e) {
            DependencyObject src = e.OriginalSource as DependencyObject;

            // ★重要：入力欄内判定を TextBox ではなく「Border全体」にする（MainWindowと同じ体感）
            bool inQuizInputArea =
                (src != null && QuizSearchBoxBorder != null && IsDescendant(src, QuizSearchBoxBorder));

            // TextBox外クリックでフォーカス解除（ただし入力欄Border内は外扱いにしない）
            if (QuizSearchTextBox != null && QuizSearchTextBox.IsKeyboardFocusWithin) {
                if (!inQuizInputArea) {
                    Keyboard.ClearFocus();
                }
            }

            // ★候補Popup外クリックで閉じる（ただし入力欄Border内は外扱いにしない）
            if (QuizWorkSuggestPopup != null && QuizWorkSuggestPopup.IsOpen) {
                bool insideQuizBox = inQuizInputArea;
                bool insideQuizSuggest = (src != null && QuizWorkSuggestBorder != null && IsDescendant(src, QuizWorkSuggestBorder));
                if (!insideQuizBox && !insideQuizSuggest) {
                    HideQuizWorkSuggest();
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

        private bool IsInQuizSuggestArea(DependencyObject src) {
            if (src == null) return false;

            if (QuizWorkSuggestList != null && IsDescendant(src, QuizWorkSuggestList)) return true;

            if (QuizWorkSuggestPopup != null) {
                var child = QuizWorkSuggestPopup.Child as DependencyObject;
                if (child != null && IsDescendant(src, child)) return true;
            }

            return false;
        }

        // =========================================================
        // ホバー：ヘッダー/メニュー（Movie/Tv/Quiz）
        // =========================================================
        private void MovieHeader_MouseEnter(object sender, MouseEventArgs e) {
            CancelMenuHide();
            if (MovieMenu != null) MovieMenu.Visibility = Visibility.Visible;
            if (TvMenu != null) TvMenu.Visibility = Visibility.Collapsed;
            if (QuizMenu != null) QuizMenu.Visibility = Visibility.Collapsed;
            HideQuizSearchPanel();
        }

        private void MovieHeader_MouseLeave(object sender, MouseEventArgs e) {
            ScheduleMenuHide();
        }

        private void TvHeader_MouseEnter(object sender, MouseEventArgs e) {
            CancelMenuHide();
            if (TvMenu != null) TvMenu.Visibility = Visibility.Visible;
            if (MovieMenu != null) MovieMenu.Visibility = Visibility.Collapsed;
            if (QuizMenu != null) QuizMenu.Visibility = Visibility.Collapsed;
            HideQuizSearchPanel();
        }

        private void TvHeader_MouseLeave(object sender, MouseEventArgs e) {
            ScheduleMenuHide();
        }

        // ★XAMLが呼んでるので必須
        private void QuizHeader_MouseEnter(object sender, MouseEventArgs e) {
            CancelMenuHide();
            if (QuizMenu != null) QuizMenu.Visibility = Visibility.Visible;
            if (MovieMenu != null) MovieMenu.Visibility = Visibility.Collapsed;
            if (TvMenu != null) TvMenu.Visibility = Visibility.Collapsed;
        }

        // ★XAMLが呼んでるので必須
        private void QuizHeader_MouseLeave(object sender, MouseEventArgs e) {
            ScheduleMenuHide();
        }

        private void Menu_MouseEnter(object sender, MouseEventArgs e) {
            CancelMenuHide();
        }

        private void Menu_MouseLeave(object sender, MouseEventArgs e) {
            if (QuizSearchTextBox != null && QuizSearchTextBox.IsKeyboardFocusWithin) return;
            ScheduleMenuHide();
        }

        private void HideMenus() {
            if (QuizSearchTextBox != null && QuizSearchTextBox.IsKeyboardFocusWithin) return;

            if (MovieMenu != null) MovieMenu.Visibility = Visibility.Collapsed;
            if (TvMenu != null) TvMenu.Visibility = Visibility.Collapsed;
            if (QuizMenu != null) QuizMenu.Visibility = Visibility.Collapsed;

            HideQuizSearchPanel();
            HideQuizWorkSuggest();
        }

        private void CancelMenuHide() => _menuHideTimer.Stop();

        private void ScheduleMenuHide() {
            _menuHideTimer.Stop();
            _menuHideTimer.Start();
        }

        // =========================================================
        // クイズ：検索パネル（ホバーで表示）
        // =========================================================
        // ★XAMLが呼んでるので必須
        private void QuizSearchHit_MouseEnter(object sender, MouseEventArgs e) {
            CancelMenuHide();
            if (QuizMenu != null) QuizMenu.Visibility = Visibility.Visible;
            ShowQuizSearchPanel();
        }

        // ★XAMLが呼んでるので必須
        private void QuizSearchHit_MouseLeave(object sender, MouseEventArgs e) {
            ScheduleMenuHide();
        }

        // ★XAMLが呼んでるので必須
        private void QuizSearchPanel_MouseEnter(object sender, MouseEventArgs e) {
            CancelMenuHide();
        }

        // ★XAMLが呼んでるので必須
        private void QuizSearchPanel_MouseLeave(object sender, MouseEventArgs e) {
            if (QuizSearchTextBox != null && QuizSearchTextBox.IsKeyboardFocusWithin) return;
            ScheduleMenuHide();
        }

        private void ShowQuizSearchPanel() {
            if (QuizSearchPanel != null) QuizSearchPanel.Visibility = Visibility.Visible;
        }

        private void HideQuizSearchPanel() {
            if (QuizSearchTextBox != null && QuizSearchTextBox.IsKeyboardFocusWithin) return;

            if (QuizSearchPanel != null) QuizSearchPanel.Visibility = Visibility.Collapsed;
            HideQuizWorkSuggest();
        }

        // =========================================================
        // ★SearchResultWindowと同じ：現在テキストで候補を即表示（キャッシュがあれば開くだけ）
        // =========================================================
        private void ShowSuggestForCurrentText(bool immediate) {
            if (QuizSearchTextBox == null) return;
            if (!(QuizSearchTextBox.IsKeyboardFocusWithin || QuizSearchTextBox.IsFocused)) return;

            string q = (QuizSearchTextBox.Text ?? "").Trim();

            // 同一クエリの候補が残っている → 開くだけ（重いの回避）
            if (_lastSuggestQueryText != null &&
                string.Equals(q, _lastSuggestQueryText, StringComparison.Ordinal) &&
                _quizWorkSuggestions.Count > 0) {
                ShowQuizWorkSuggest();
                return;
            }

            if (string.IsNullOrWhiteSpace(q)) {
                _ = RunQuizSuggestEmptyAsync(); // ここで _lastSuggestQueryText を "" に更新
                return;
            }

            if (immediate) {
                _ = RunQuizWorkSuggestAsync(q); // デバウンス無し（1クリックで出す）
            } else {
                StartQuizWorkSuggestDebounce(q);
            }
        }

        // =========================================================
        // ★MainWindowと同じ：入力欄Borderクリックで「1回で」候補を出す
        // =========================================================
        private void QuizSearchBoxBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
            if (QuizSearchTextBox == null) return;

            // TextBox自体をクリックした場合は、TextBox側の既存処理に任せる（余計な変更をしない）
            var src = e.OriginalSource as DependencyObject;
            if (src != null && IsDescendant(src, QuizSearchTextBox)) return;

            CancelMenuHide();
            if (QuizMenu != null) QuizMenu.Visibility = Visibility.Visible;
            if (QuizSearchPanel != null) QuizSearchPanel.Visibility = Visibility.Visible;

            if (!QuizSearchTextBox.IsKeyboardFocusWithin) {
                e.Handled = true;
                QuizSearchTextBox.Focus();
            }

            QuizSearchTextBox.IsReadOnlyCaretVisible = true;

            // ★フォーカス反映後に必ず判定（1回で出る）+ 毎回先頭へ
            Dispatcher.BeginInvoke(new Action(() => {
                ResetSuggestScrollState();
                ShowSuggestForCurrentText(immediate: true);
            }), DispatcherPriority.Input);
        }

        // =========================================================
        // ★MainWindowと同じ：プレースホルダー(TextBlock)クリック時の処理
        // =========================================================
        private void QuizSearchPlaceholder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
            if (QuizSearchBoxBorder != null) {
                QuizSearchBoxBorder_PreviewMouseLeftButtonDown(QuizSearchBoxBorder, e);
            } else {
                if (QuizSearchTextBox == null) return;

                CancelMenuHide();
                if (QuizMenu != null) QuizMenu.Visibility = Visibility.Visible;
                if (QuizSearchPanel != null) QuizSearchPanel.Visibility = Visibility.Visible;

                if (!QuizSearchTextBox.IsKeyboardFocusWithin) {
                    QuizSearchTextBox.Focus();
                }

                QuizSearchTextBox.IsReadOnlyCaretVisible = true;

                Dispatcher.BeginInvoke(new Action(() => {
                    ResetSuggestScrollState();
                    ShowSuggestForCurrentText(immediate: true);
                }), DispatcherPriority.Input);
            }

            e.Handled = true;
        }

        // ★XAMLが呼んでるので必須
        private void QuizSearch_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
            if (QuizSearchTextBox == null) return;

            CancelMenuHide();
            if (QuizMenu != null) QuizMenu.Visibility = Visibility.Visible;
            if (QuizSearchPanel != null) QuizSearchPanel.Visibility = Visibility.Visible;

            if (!QuizSearchTextBox.IsKeyboardFocusWithin) {
                e.Handled = true;
                QuizSearchTextBox.Focus();
            }

            // ★シングルクリックで必ず候補（毎回先頭）
            Dispatcher.BeginInvoke(new Action(() => {
                ResetSuggestScrollState();
                ShowSuggestForCurrentText(immediate: true);
            }), DispatcherPriority.Input);
        }

        // ★XAMLが呼んでるので必須
        private void QuizSearch_GotFocus(object sender, RoutedEventArgs e) {
            if (QuizSearchTextBox == null) return;

            CancelMenuHide();
            if (QuizMenu != null) QuizMenu.Visibility = Visibility.Visible;
            if (QuizSearchPanel != null) QuizSearchPanel.Visibility = Visibility.Visible;

            QuizSearchTextBox.IsReadOnlyCaretVisible = true;

            // ★フォーカス時に即表示（毎回先頭）
            Dispatcher.BeginInvoke(new Action(() => {
                ResetSuggestScrollState();
                ShowSuggestForCurrentText(immediate: true);
            }), DispatcherPriority.Input);
        }

        // ★XAMLが呼んでるので必須
        private void QuizSearch_LostFocus(object sender, RoutedEventArgs e) {
            if (QuizSearchTextBox == null) return;

            QuizSearchTextBox.IsReadOnlyCaretVisible = false;

            var fe = Keyboard.FocusedElement as DependencyObject;
            if (fe == null || !IsInQuizSuggestArea(fe)) {
                HideQuizWorkSuggest();
            }

            ScheduleMenuHide();
        }

        // ★XAMLが呼んでるので必須
        private void QuizSearch_TextChanged(object sender, TextChangedEventArgs e) {
            if (QuizSearchTextBox == null) return;
            if (_suppressQuizSuggest) return;

            if (QuizSearchTextBox.IsKeyboardFocusWithin) CancelMenuHide();

            string queryText = (QuizSearchTextBox.Text ?? "").Trim();

            if (string.IsNullOrWhiteSpace(queryText)) {
                if (QuizSearchTextBox.IsKeyboardFocusWithin || QuizSearchTextBox.IsFocused) {
                    // ★空になったら全件（ただしキャッシュがあれば開くだけ）
                    ShowSuggestForCurrentText(immediate: true);
                } else {
                    HideQuizWorkSuggest();
                }
                return;
            }

            StartQuizWorkSuggestDebounce(queryText);
        }

        // ★XAMLが呼んでるので必須
        private async void QuizSearch_PreviewKeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Escape) {
                HideQuizWorkSuggest();
                Keyboard.ClearFocus();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter) {
                e.Handled = true;
                await TryStartQuizAsync();
            }
        }

        // ★XAMLが呼んでるので必須（クイズ開始ボタン）
        private async void QuizSearchHit_Click(object sender, RoutedEventArgs e) {
            CancelMenuHide();
            if (QuizMenu != null) QuizMenu.Visibility = Visibility.Visible;

            if (QuizSearchPanel != null && QuizSearchPanel.Visibility != Visibility.Visible) {
                ShowQuizSearchPanel();
            }

            if (QuizSearchTextBox != null && !QuizSearchTextBox.IsKeyboardFocusWithin) {
                QuizSearchTextBox.Focus();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            }

            string title = (QuizSearchTextBox?.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(title)) {
                ResetSuggestScrollState();
                ShowSuggestForCurrentText(immediate: true);
                return;
            }

            await TryStartQuizAsync();
        }

        // ★クイズが無いなら何もしない（画面遷移しない）
        private async Task TryStartQuizAsync() {
            string title = (QuizSearchTextBox != null ? (QuizSearchTextBox.Text ?? "").Trim() : "");
            if (string.IsNullOrWhiteSpace(title)) return;

            await AppDb.InitAsync();

            Work work = await AppDb.Connection.Table<Work>()
                .Where(w => w.Title == title)
                .FirstOrDefaultAsync();

            if (work == null) return;

            List<Quiz> quizzes = await AppDb.Connection.Table<Quiz>()
                .Where(qq => qq.WorkKey == work.WorkKey)
                .ToListAsync();

            if (quizzes == null || quizzes.Count <= 0) return;

            int firstQuizId = quizzes.OrderBy(x => x.QuizId).First().QuizId;

            HideQuizWorkSuggest();

            await OpenQuizPlayByWorkKeyAsync(work.WorkKey);
        }

        // =========================================================
        // ★クイズ候補クリックを確実に拾う（Preview）
        // =========================================================
        private async void QuizWorkSuggestList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
            if (QuizWorkSuggestList == null) return;

            var dep = e.OriginalSource as DependencyObject;
            if (dep == null) return;

            var lbi = ItemsControl.ContainerFromElement(QuizWorkSuggestList, dep) as ListBoxItem;
            var it = (lbi != null) ? (lbi.DataContext as QuizWorkSuggestItem) : null;
            if (it == null) return;

            e.Handled = true;
            await OpenQuizPlayByWorkKeyAsync(it.WorkKey);
        }

        // =========================================================
        // ★クイズ候補（DB：クイズが存在する作品）表示/取得（インデックス1回読み込み→メモリ検索）
        // =========================================================
        private void StartQuizWorkSuggestDebounce(string queryText) {
            _pendingQuizSuggestQuery = (queryText ?? "").Trim();
            _quizSuggestDebounceTimer.Stop();
            _quizSuggestDebounceTimer.Start();
        }

        private async Task EnsureQuizIndexAsync(CancellationToken token) {
            if (_quizIndexLoaded) return;

            await _quizIndexGate.WaitAsync(token);
            try {
                if (_quizIndexLoaded) return;

                await AppDb.InitAsync();
                if (token.IsCancellationRequested) return;

                // ★クイズ数ズレ対策：QuizIdの重複を除外して数える
                var rows = await AppDb.Connection.QueryAsync<QuizSuggestRow>(
                    "SELECT w.WorkKey as WorkKey, w.Title as Title, w.PosterPath as PosterPath, qc.QuizCount as QuizCount " +
                    "FROM [Work] w " +
                    "JOIN (SELECT WorkKey, COUNT(DISTINCT QuizId) as QuizCount FROM Quiz GROUP BY WorkKey) qc " +
                    "ON (qc.WorkKey COLLATE BINARY) = (w.WorkKey COLLATE BINARY) " +
                    "ORDER BY w.Title COLLATE NOCASE"
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

                // キャッシュがあって同一クエリなら開くだけ（重いの回避）
                if (_lastSuggestQueryText != null &&
                    string.Equals("", _lastSuggestQueryText, StringComparison.Ordinal) &&
                    _quizWorkSuggestions.Count > 0) {

                    await Dispatcher.InvokeAsync(() => {
                        if (mySeq != _quizSuggestSeq) return;
                        if (QuizSearchTextBox == null) return;
                        if (!(QuizSearchTextBox.IsKeyboardFocusWithin || QuizSearchTextBox.IsFocused)) return;
                        if (!string.IsNullOrWhiteSpace(QuizSearchTextBox.Text)) return;

                        ShowQuizWorkSuggest();
                    });

                    return;
                }

                var src = _quizIndex;

                var list = await Task.Run(() => {
                    return src
                        .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Title))
                        .OrderBy(r => r.Title, StringComparer.CurrentCulture)
                        .Select(r => new QuizWorkSuggestItem {
                            WorkKey = r.WorkKey ?? "",
                            Title = r.Title ?? "",
                            PosterThumbUrl = BuildPosterThumbUrlFromStoredPath(r.PosterPath),
                            Sub = "クイズ数：" + r.QuizCount.ToString()
                        })
                        .ToList();
                }, token);

                if (token.IsCancellationRequested) return;
                if (mySeq != _quizSuggestSeq) return;

                await Dispatcher.InvokeAsync(() => {
                    if (mySeq != _quizSuggestSeq) return;

                    if (QuizSearchTextBox == null) return;
                    if (!(QuizSearchTextBox.IsKeyboardFocusWithin || QuizSearchTextBox.IsFocused)) return;
                    if (!string.IsNullOrWhiteSpace(QuizSearchTextBox.Text)) return;

                    _quizWorkSuggestions.Clear();
                    for (int i = 0; i < list.Count; i++) _quizWorkSuggestions.Add(list[i]);

                    _lastSuggestQueryText = ""; // ★ここで更新

                    if (_quizWorkSuggestions.Count > 0) ShowQuizWorkSuggest();
                    else HideQuizWorkSuggest();
                });
            }
            catch {
                await Dispatcher.InvokeAsync(() => HideQuizWorkSuggest());
            }
        }

        private async Task RunQuizWorkSuggestAsync(string queryText) {
            long mySeq = Interlocked.Increment(ref _quizSuggestSeq);

            if (_ctsQuizSuggest != null) _ctsQuizSuggest.Cancel();
            _ctsQuizSuggest = new CancellationTokenSource();
            var token = _ctsQuizSuggest.Token;

            queryText = (queryText ?? "").Trim();

            try {
                await EnsureQuizIndexAsync(token);
                if (token.IsCancellationRequested) return;
                if (mySeq != _quizSuggestSeq) return;

                // キャッシュがあって同一クエリなら開くだけ（重いの回避）
                if (_lastSuggestQueryText != null &&
                    string.Equals(queryText, _lastSuggestQueryText, StringComparison.Ordinal) &&
                    _quizWorkSuggestions.Count > 0) {

                    await Dispatcher.InvokeAsync(() => {
                        if (mySeq != _quizSuggestSeq) return;
                        if (QuizSearchTextBox == null) return;
                        if (!(QuizSearchTextBox.IsKeyboardFocusWithin || QuizSearchTextBox.IsFocused)) return;

                        ShowQuizWorkSuggest();
                    });

                    return;
                }

                var src = _quizIndex;

                var list = await Task.Run(() => {
                    var ret = new List<QuizWorkSuggestItem>();

                    for (int i = 0; i < src.Count; i++) {
                        if (token.IsCancellationRequested) break;

                        var r = src[i];
                        if (r == null) continue;

                        string title = r.Title ?? "";
                        if (title.Length == 0) continue;

                        if (!JaContains(title, queryText)) continue;

                        ret.Add(new QuizWorkSuggestItem {
                            WorkKey = r.WorkKey ?? "",
                            Title = title,
                            PosterThumbUrl = BuildPosterThumbUrlFromStoredPath(r.PosterPath),
                            Sub = "クイズ数：" + r.QuizCount.ToString()
                        });
                    }

                    return ret
                        .OrderByDescending(x => JaStartsWith(x.Title, queryText))
                        .ThenBy(x => x.Title, StringComparer.CurrentCulture)
                        .ToList();
                }, token);

                if (token.IsCancellationRequested) return;
                if (mySeq != _quizSuggestSeq) return;

                await Dispatcher.InvokeAsync(() => {
                    if (mySeq != _quizSuggestSeq) return;

                    _quizWorkSuggestions.Clear();
                    for (int i = 0; i < list.Count; i++) _quizWorkSuggestions.Add(list[i]);

                    _lastSuggestQueryText = queryText; // ★ここで更新

                    if (_quizWorkSuggestions.Count > 0 && QuizSearchTextBox != null &&
                        (QuizSearchTextBox.IsKeyboardFocusWithin || QuizSearchTextBox.IsFocused)) {
                        ShowQuizWorkSuggest();
                    } else {
                        HideQuizWorkSuggest();
                    }
                });
            }
            catch {
                await Dispatcher.InvokeAsync(() => HideQuizWorkSuggest());
            }
        }

        // =========================================================
        // ★候補Popup表示：毎回「必ず先頭」＆「一番下へ復元」を潰す（SearchResultWindowと同じ）
        // =========================================================
        private void ShowQuizWorkSuggest() {
            if (QuizWorkSuggestPopup == null) return;
            if (QuizSearchTextBox == null) return;

            if (!(QuizSearchTextBox.IsKeyboardFocusWithin || QuizSearchTextBox.IsFocused)) return;
            if (QuizSearchPanel != null && QuizSearchPanel.Visibility != Visibility.Visible) return;

            int seq = ++_suggestShowSeq;

            // 先頭固定（開く前）
            ResetSuggestScrollState();

            // 一瞬下が見えるのを防ぐ
            SetSuggestChildVisibility(Visibility.Hidden);

            if (QuizWorkSuggestBorder != null) QuizWorkSuggestBorder.Visibility = Visibility.Visible;

            // 開き直して確実に出す
            QuizWorkSuggestPopup.IsOpen = false;
            QuizWorkSuggestPopup.IsOpen = true;

            // 先頭固定 → 表示（軽く2回）
            Dispatcher.BeginInvoke(new Action(() => {
                if (seq != _suggestShowSeq) return;
                ForceSuggestTopOnce();
            }), DispatcherPriority.Loaded);

            Dispatcher.BeginInvoke(new Action(() => {
                if (seq != _suggestShowSeq) return;
                ForceSuggestTopOnce();
                SetSuggestChildVisibility(Visibility.Visible);
            }), DispatcherPriority.Render);

            // 保険
            Dispatcher.BeginInvoke(new Action(() => {
                if (seq != _suggestShowSeq) return;
                SetSuggestChildVisibility(Visibility.Visible);
            }), DispatcherPriority.Background);
        }

        private void HideQuizWorkSuggest() {
            try { _quizSuggestDebounceTimer.Stop(); } catch { }
            _pendingQuizSuggestQuery = "";

            // ★閉じる前に先頭へ戻して復元を潰す
            ResetSuggestScrollState();

            if (QuizWorkSuggestPopup != null) QuizWorkSuggestPopup.IsOpen = false;
            if (QuizWorkSuggestBorder != null) QuizWorkSuggestBorder.Visibility = Visibility.Collapsed;

            if (QuizWorkSuggestList != null) {
                try { QuizWorkSuggestList.UnselectAll(); } catch { }
                QuizWorkSuggestList.SelectedIndex = -1;
                QuizWorkSuggestList.SelectedItem = null;
            }

            SetSuggestChildVisibility(Visibility.Visible);

            // ★候補はクリアしない（次回同一クエリは開くだけにして重さ回避）
        }

        private void SetSuggestChildVisibility(Visibility v) {
            try {
                if (QuizWorkSuggestBorder != null) {
                    if (v == Visibility.Hidden) {
                        if (QuizWorkSuggestBorder.Visibility != Visibility.Collapsed) QuizWorkSuggestBorder.Visibility = Visibility.Hidden;
                    } else if (v == Visibility.Visible) {
                        if (QuizWorkSuggestBorder.Visibility != Visibility.Collapsed) QuizWorkSuggestBorder.Visibility = Visibility.Visible;
                    } else {
                        QuizWorkSuggestBorder.Visibility = v;
                    }
                    return;
                }

                if (QuizWorkSuggestPopup == null) return;
                var ui = QuizWorkSuggestPopup.Child as UIElement;
                if (ui == null) return;
                ui.Visibility = v;
            }
            catch {
            }
        }

        private void ResetSuggestScrollState() {
            try {
                if (QuizWorkSuggestList == null) return;

                try { QuizWorkSuggestList.UnselectAll(); } catch { }
                try { QuizWorkSuggestList.SelectedIndex = -1; } catch { }
                try { QuizWorkSuggestList.SelectedItem = null; } catch { }

                var sv = GetSuggestScrollViewer();
                if (sv != null) {
                    sv.ScrollToVerticalOffset(0);
                    sv.ScrollToTop();
                }
            }
            catch {
            }
        }

        private void ForceSuggestTopOnce() {
            try {
                if (QuizWorkSuggestList == null) return;

                try { QuizWorkSuggestList.UnselectAll(); } catch { }
                try { QuizWorkSuggestList.SelectedIndex = -1; } catch { }
                try { QuizWorkSuggestList.SelectedItem = null; } catch { }

                QuizWorkSuggestList.UpdateLayout();

                var sv = GetSuggestScrollViewer();
                if (sv != null) {
                    sv.UpdateLayout();
                    sv.ScrollToVerticalOffset(0);
                    sv.ScrollToTop();
                }

                if (QuizWorkSuggestList.Items != null && QuizWorkSuggestList.Items.Count > 0) {
                    var first = QuizWorkSuggestList.Items[0];
                    QuizWorkSuggestList.ScrollIntoView(first);
                }

                QuizWorkSuggestList.UpdateLayout();

                if (sv != null) {
                    sv.UpdateLayout();
                    sv.ScrollToVerticalOffset(0);
                    sv.ScrollToTop();
                }
            }
            catch {
            }
        }

        private ScrollViewer GetSuggestScrollViewer() {
            try {
                if (_suggestScrollViewer != null) return _suggestScrollViewer;
                if (QuizWorkSuggestList == null) return null;

                _suggestScrollViewer = FindVisualChild<ScrollViewer>(QuizWorkSuggestList);
                return _suggestScrollViewer;
            }
            catch {
                _suggestScrollViewer = null;
                return null;
            }
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject {
            if (parent == null) return null;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++) {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T) return (T)child;

                var hit = FindVisualChild<T>(child);
                if (hit != null) return hit;
            }
            return null;
        }

        // ★XAMLが呼んでるので必須（バブル側：保険）
        private async void QuizWorkSuggestList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
            if (QuizWorkSuggestList == null) return;
            var it = QuizWorkSuggestList.SelectedItem as QuizWorkSuggestItem;
            if (it == null) return;

            await OpenQuizPlayByWorkKeyAsync(it.WorkKey);
        }

        private static string BuildPosterThumbUrlFromStoredPath(string posterPathOrUrl) {
            if (string.IsNullOrWhiteSpace(posterPathOrUrl)) return "";
            string p = posterPathOrUrl.Trim();

            if (p.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) {
                return p;
            }

            if (!p.StartsWith("/")) p = "/" + p;
            return "https://image.tmdb.org/t/p/w92" + p;
        }

        private class QuizWorkSuggestItem {
            public string WorkKey { get; set; }
            public string Title { get; set; }
            public string Sub { get; set; }
            public string PosterThumbUrl { get; set; }
        }

        private class QuizSuggestRow {
            public string WorkKey { get; set; }
            public string Title { get; set; }
            public string PosterPath { get; set; }
            public int QuizCount { get; set; }
        }

        // =========================================================
        // ★WorkKeyで直接クイズ回答へ（候補クリックで確実に遷移）
        // =========================================================
        private async Task OpenQuizPlayByWorkKeyAsync(string workKey) {
            workKey = (workKey ?? "").Trim();
            if (string.IsNullOrWhiteSpace(workKey)) return;

            try {
                HideQuizWorkSuggest();

                if (QuizSearchPanel != null) QuizSearchPanel.Visibility = Visibility.Collapsed;
                if (QuizMenu != null) QuizMenu.Visibility = Visibility.Collapsed;

                ClearQuizSearchUi();

                await AppDb.InitAsync();

                var quizzes = await AppDb.Connection.Table<Quiz>()
                    .Where(qq => qq.WorkKey == workKey)
                    .ToListAsync();

                if (quizzes == null || quizzes.Count == 0) return;

                int firstQuizId = quizzes.OrderBy(x => x.QuizId).First().QuizId;

                Window quizWin = CreateQuizPlayWindowWindow(workKey, firstQuizId);
                if (quizWin == null) return;

                quizWin.Owner = this;

                this.Hide();
                quizWin.Closed += (_, __) => {
                    if (AppNav.ForceMain) return;
                    try { this.Show(); this.Activate(); } catch { }
                };

                quizWin.WindowState = WindowState.Maximized;
                quizWin.Show();
            }
            catch {
            }
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

        private void ClearQuizSearchUi() {
            HideQuizWorkSuggest();

            if (QuizSearchTextBox != null) {
                _suppressQuizSuggest = true;
                QuizSearchTextBox.Text = "";
                _suppressQuizSuggest = false;

                QuizSearchTextBox.IsReadOnlyCaretVisible = false;
            }
        }

        // =========================================================
        // ★追加：モード切替（人気/上映中/放送中）時は必ず最上部へ
        // =========================================================
        private void ScrollToTopIfPossible() {
            try {
                if (MainScrollViewer != null) MainScrollViewer.ScrollToTop();
            }
            catch { }
        }

        // =========================================================
        // メニュークリック：モード切替
        // =========================================================
        private async void MoviePopular_Click(object sender, RoutedEventArgs e) { await SetModeAsync(BrowseMode.MoviePopular); }
        private async void MovieNowPlaying_Click(object sender, RoutedEventArgs e) { await SetModeAsync(BrowseMode.MovieNowPlaying); }
        private async void TvPopular_Click(object sender, RoutedEventArgs e) { await SetModeAsync(BrowseMode.TvPopular); }
        private async void TvOnAir_Click(object sender, RoutedEventArgs e) { await SetModeAsync(BrowseMode.TvOnAir); }

        private async Task SetModeAsync(BrowseMode mode) {
            ClearQuizSearchUi();

            // ★人気/上映中/放送中 押下で必ず最上部
            ScrollToTopIfPossible();

            _mode = mode;
            HideMenus();
            ApplyModeTitle();

            try { await LoadGenresAsync(); } catch { }

            _genreId = -1;
            for (int i = 0; i < Genres.Count; i++)
                Genres[i].IsSelected = (Genres[i].Id == -1);

            await ResetAndLoadAsync();

            // ★読み込み後も確実に最上部（レイアウト反映後）
            await Dispatcher.InvokeAsync(() => {
                ScrollToTopIfPossible();
            }, DispatcherPriority.Background);
        }

        private void ApplyModeTitle() {
            if (CategoryText == null) return;

            if (_mode == BrowseMode.MoviePopular) CategoryText.Text = "人気の映画";
            else if (_mode == BrowseMode.MovieNowPlaying) CategoryText.Text = "上映中の映画";
            else if (_mode == BrowseMode.TvPopular) CategoryText.Text = "人気のテレビ番組";
            else CategoryText.Text = "現在放送中のテレビ番組";
        }

        private bool IsMovieMode() {
            return _mode == BrowseMode.MoviePopular || _mode == BrowseMode.MovieNowPlaying;
        }

        // =========================================================
        // ジャンル
        // =========================================================
        private async Task LoadGenresAsync() {
            Genres.Clear();
            Genres.Add(new GenreOption { Id = -1, Name = "すべて", IsSelected = true });

            string mediaType = IsMovieMode() ? "movie" : "tv";
            string url = "https://api.themoviedb.org/3/genre/" + mediaType + "/list?api_key=" + ApiKey + "&language=ja-JP";

            string json;
            try { json = await _http.GetStringAsync(url); }
            catch { return; }

            JObject obj;
            try { obj = JObject.Parse(json); }
            catch { return; }

            JArray genres = obj["genres"] as JArray;
            if (genres == null) return;

            List<GenreOption> list = new List<GenreOption>();
            for (int i = 0; i < genres.Count; i++) {
                JToken g = genres[i];
                int id = g["id"] != null ? g["id"].Value<int>() : 0;
                string name = g["name"] != null ? g["name"].ToString() : "";
                if (id != 0 && !string.IsNullOrWhiteSpace(name))
                    list.Add(new GenreOption { Id = id, Name = name, IsSelected = false });
            }

            foreach (GenreOption it in list.OrderBy(x => x.Name))
                Genres.Add(it);

            if (!IsMovieMode()) return;

            for (int i = Genres.Count - 1; i >= 0; i--) {
                var name = (Genres[i]?.Name ?? "").Trim();
                if (name == "履歴") {
                    Genres.RemoveAt(i);
                }
            }
        }

        private async void Genre_Click(object sender, RoutedEventArgs e) {
            ToggleButton tb = sender as ToggleButton;
            if (tb == null) return;

            int id;
            try { id = Convert.ToInt32(tb.Tag); }
            catch { return; }

            _genreId = id;

            for (int i = 0; i < Genres.Count; i++)
                Genres[i].IsSelected = (Genres[i].Id == id);

            await ResetAndLoadAsync();
        }

        // =========================================================
        // 一覧：読み込み
        // =========================================================
        private async Task ResetAndLoadAsync() {
            try { _cts?.Cancel(); } catch { }
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            Items.Clear();

            SetLoadMoreBarVisible(false);

            _apiPage = 1;
            _apiTotalPages = int.MaxValue;

            _buffer.Clear();
            _bufferIndex = 0;

            _seenKeys.Clear();

            await FillBufferIfNeededAsync(token);
            await AppendNextChunkAsync(token);
        }

        private void SetLoadMoreBarVisible(bool visible) {
            if (LoadMoreBar == null) return;
            LoadMoreBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void LoadMore_Click(object sender, RoutedEventArgs e) {
            if (_cts == null) return;

            RemoveLoadMoreCard();
            if (LoadMoreBar != null) LoadMoreBar.IsEnabled = false;

            await AppendNextChunkAsync(_cts.Token);

            if (LoadMoreBar != null) LoadMoreBar.IsEnabled = true;
        }

        private void RemoveLoadMoreCard() {
            SetLoadMoreBarVisible(false);
        }

        private void AddLoadMoreCardIfHasMore() {
            bool hasMore = (_bufferIndex < _buffer.Count) || (_apiPage <= _apiTotalPages);
            SetLoadMoreBarVisible(hasMore);
        }

        private async Task FillBufferIfNeededAsync(CancellationToken token) {
            if (token.IsCancellationRequested) return;
            if (_bufferIndex < _buffer.Count) return;
            if (_apiPage > _apiTotalPages) return;

            PageResult page = await FetchPageAsync(_apiPage, token);
            if (page == null) {
                _apiTotalPages = _apiPage - 1;
                return;
            }

            _apiTotalPages = page.TotalPages;
            _apiPage++;

            for (int i = 0; i < page.Items.Count; i++) {
                MediaItem m = page.Items[i];
                string key = m.MediaType + ":" + m.Id;
                if (_seenKeys.Add(key))
                    _buffer.Add(m);
            }
        }

        private async Task AppendNextChunkAsync(CancellationToken token) {
            if (token.IsCancellationRequested) return;

            List<MediaItem> added = new List<MediaItem>();

            int guard = 0;
            while (added.Count < ChunkSize && guard < 700) {
                if (token.IsCancellationRequested) return;

                if (_bufferIndex >= _buffer.Count)
                    await FillBufferIfNeededAsync(token);

                if (_bufferIndex >= _buffer.Count) break;

                MediaItem item = _buffer[_bufferIndex];
                _bufferIndex++;

                if (_genreId != -1) {
                    if (item.GenreIds == null || !item.GenreIds.Contains(_genreId)) {
                        guard++;
                        continue;
                    }
                }

                added.Add(item);
                guard++;
            }

            for (int i = 0; i < added.Count; i++)
                Items.Add(BrowserRow.FromMedia(added[i]));

            if (_bufferIndex >= _buffer.Count)
                await FillBufferIfNeededAsync(token);

            AddLoadMoreCardIfHasMore();
        }

        // =========================================================
        // TMDB：ページ取得
        // =========================================================
        private async Task<PageResult> FetchPageAsync(int page, CancellationToken token) {
            if (token.IsCancellationRequested) return null;

            string url = BuildUrl(page);
            if (string.IsNullOrWhiteSpace(url)) return null;

            string json;
            try { json = await _http.GetStringAsync(url); }
            catch { return null; }

            JObject obj;
            try { obj = JObject.Parse(json); }
            catch { return null; }

            int totalPages = obj["total_pages"] != null ? obj["total_pages"].Value<int>() : 1;

            JArray results = obj["results"] as JArray;
            if (results == null) return new PageResult { TotalPages = totalPages, Items = new List<MediaItem>() };

            List<MediaItem> items = new List<MediaItem>();

            for (int i = 0; i < results.Count; i++) {
                JToken r = results[i];

                int id = r["id"] != null ? r["id"].Value<int>() : 0;
                if (id == 0) continue;

                string poster = r["poster_path"] != null ? r["poster_path"].ToString() : "";
                if (string.IsNullOrWhiteSpace(poster)) continue;

                double vote = r["vote_average"] != null ? r["vote_average"].Value<double>() : 0.0;

                string title = "";
                if (IsMovieMode()) {
                    title = r["title"] != null ? r["title"].ToString() : "";
                    if (string.IsNullOrWhiteSpace(title) && r["original_title"] != null)
                        title = r["original_title"].ToString();
                } else {
                    title = r["name"] != null ? r["name"].ToString() : "";
                    if (string.IsNullOrWhiteSpace(title) && r["original_name"] != null)
                        title = r["original_name"].ToString();
                }
                if (string.IsNullOrWhiteSpace(title)) title = "(タイトル不明)";

                string rawDate = "";
                if (IsMovieMode()) {
                    if (r["release_date"] != null) rawDate = r["release_date"].ToString();
                } else {
                    if (r["first_air_date"] != null) rawDate = r["first_air_date"].ToString();
                }

                string dateLabel = IsMovieMode() ? "公開日" : "放送日";
                string formatted = "-";
                DateTime dt;
                if (!string.IsNullOrWhiteSpace(rawDate) &&
                    DateTime.TryParseExact(rawDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) {
                    formatted = dt.ToString("yyyy年M月d日", CultureInfo.GetCultureInfo("ja-JP"));
                }
                string dateText = dateLabel + ": " + formatted;

                List<int> genreIds = new List<int>();
                JArray g = r["genre_ids"] as JArray;
                if (g != null) {
                    for (int j = 0; j < g.Count; j++)
                        if (g[j] != null && g[j].Type == JTokenType.Integer)
                            genreIds.Add(g[j].Value<int>());
                }

                string mediaType = IsMovieMode() ? "movie" : "tv";

                items.Add(new MediaItem {
                    Id = id,
                    MediaType = mediaType,
                    Title = title,
                    PosterPath = poster,
                    PosterUrl = BuildPosterUrl(poster),
                    VoteAverage = vote,
                    GenreIds = genreIds,
                    AirDateText = dateText
                });
            }

            return new PageResult { TotalPages = totalPages, Items = items };
        }

        private string BuildUrl(int page) {
            string baseUrl = "";

            if (_mode == BrowseMode.MoviePopular) baseUrl = "https://api.themoviedb.org/3/movie/popular";
            else if (_mode == BrowseMode.MovieNowPlaying) baseUrl = "https://api.themoviedb.org/3/movie/now_playing";
            else if (_mode == BrowseMode.TvPopular) baseUrl = "https://api.themoviedb.org/3/tv/popular";
            else if (_mode == BrowseMode.TvOnAir) baseUrl = "https://api.themoviedb.org/3/tv/on_the_air";

            if (IsMovieMode())
                return baseUrl + "?api_key=" + ApiKey + "&language=ja-JP&region=JP&include_adult=false&page=" + page;

            return baseUrl + "?api_key=" + ApiKey + "&language=ja-JP&include_adult=false&page=" + page;
        }

        private static string BuildPosterUrl(string posterPath) {
            if (string.IsNullOrWhiteSpace(posterPath)) return "";
            if (!posterPath.StartsWith("/")) posterPath = "/" + posterPath;
            return "https://image.tmdb.org/t/p" + PosterSize + posterPath;
        }

        // =========================================================
        // 画像：遅延ロード
        // =========================================================
        private async void PosterImage_Loaded(object sender, RoutedEventArgs e) {
            Image img = sender as Image;
            if (img == null) return;

            string url = img.Tag as string;
            if (string.IsNullOrWhiteSpace(url)) return;
            if (img.Source != null) return;

            BitmapSource cached = null;
            lock (_imgLock) {
                BitmapSource tmp;
                if (_imgCache.TryGetValue(url, out tmp))
                    cached = tmp;
            }

            if (cached != null) {
                img.Source = cached;
                return;
            }

            CancellationToken token = (_cts != null) ? _cts.Token : CancellationToken.None;

            bool acquired = false;
            try {
                await _imgGate.WaitAsync(token);
                acquired = true;

                lock (_imgLock) {
                    BitmapSource tmp2;
                    if (_imgCache.TryGetValue(url, out tmp2)) {
                        img.Source = tmp2;
                        return;
                    }
                }

                byte[] bytes;
                try { bytes = await _http.GetByteArrayAsync(url); }
                catch { return; }

                BitmapSource bmp = CreateFrozenBitmap(bytes, 380);

                lock (_imgLock) {
                    if (!_imgCache.ContainsKey(url))
                        _imgCache[url] = bmp;
                }

                img.Source = bmp;
            }
            catch { }
            finally {
                try { if (acquired) _imgGate.Release(); } catch { }
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

        // =========================================================
        // Binding用モデル
        // =========================================================
        public class BrowserRow {
            public bool IsLoadMore { get; set; }
            public MediaItem Media { get; set; }

            public string Title { get { return Media != null ? Media.Title : ""; } }
            public string PosterUrl { get { return Media != null ? Media.PosterUrl : ""; } }
            public string AirDateText { get { return Media != null ? (Media.AirDateText ?? "") : ""; } }

            public int ScorePercent {
                get {
                    if (Media == null) return 0;
                    int pct = (int)Math.Round(Media.VoteAverage * 10.0);
                    if (pct < 0) pct = 0;
                    if (pct > 100) pct = 100;
                    return pct;
                }
            }

            public string ScoreText { get { return ScorePercent.ToString() + "%"; } }

            public static BrowserRow FromMedia(MediaItem m) { return new BrowserRow { IsLoadMore = false, Media = m }; }
            public static BrowserRow LoadMore() { return new BrowserRow { IsLoadMore = true, Media = null }; }
        }

        public class MediaItem {
            public int Id { get; set; }
            public string MediaType { get; set; } // "movie" or "tv"
            public string Title { get; set; }
            public string PosterPath { get; set; }
            public string PosterUrl { get; set; }
            public double VoteAverage { get; set; }
            public List<int> GenreIds { get; set; }
            public string AirDateText { get; set; }
        }

        private class PageResult {
            public int TotalPages { get; set; }
            public List<MediaItem> Items { get; set; }
        }

        public class GenreOption : INotifyPropertyChanged {
            public int Id { get; set; }
            public string Name { get; set; }

            private bool _isSelected;
            public bool IsSelected {
                get { return _isSelected; }
                set {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsSelected"));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        // =========================================================
        // クイズ削除（モーダルで前に出す）
        // =========================================================
        private void QuizDelete_Click(object sender, RoutedEventArgs e) {
            ClearQuizSearchUi();
            HideMenus();

            Window w = CreateWindowByTypeNames(new string[] {
                "Movie_AnimeQuizApp.Views.QuizDeleteWindow",
                "Movie_AnimeQuizApp.QuizDeleteWindow",
                "Movie_AnimeQuizApp.QuizDelete"
            });

            if (w == null) {
                MessageBox.Show("クイズ削除画面（QuizDeleteWindow）が見つかりません。");
                return;
            }

            w.Owner = this;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            w.ShowDialog();
        }

        // ★XAMLが呼んでるので必須（クイズ作成）
        private void QuizCreate_Click(object sender, RoutedEventArgs e) {
            ClearQuizSearchUi();
            HideMenus();

            Window w = CreateWindowByTypeNames(new string[] {
                "Movie_AnimeQuizApp.Views.QuizCreateWindow",
                "Movie_AnimeQuizApp.QuizCreateWindow",
                "Movie_AnimeQuizApp.QuizCreate",
                "Movie_AnimeQuizApp.QuizCreatePage"
            });

            if (w == null) {
                return;
            }

            w.Owner = this;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            this.Hide();
            w.Closed += (_, __) => {
                if (AppNav.ForceMain) return;
                try { this.Show(); this.Activate(); } catch { }
            };

            w.WindowState = WindowState.Maximized;
            w.Show();
        }

        private Window CreateWindowByTypeNames(string[] fullNames) {
            try {
                var asm = Assembly.GetExecutingAssembly();
                for (int i = 0; i < fullNames.Length; i++) {
                    var t = asm.GetType(fullNames[i], false);
                    if (t == null) continue;
                    if (!typeof(Window).IsAssignableFrom(t)) continue;

                    var obj = Activator.CreateInstance(t);
                    return obj as Window;
                }
            }
            catch { }
            return null;
        }
    }

    // =========================================================
    // 円弧ゲージ Converter（WPF用）
    // =========================================================
    public class CircularArcConverter : IValueConverter {
        private const double Thickness = 6.0;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            double percent = 0;
            try { percent = System.Convert.ToDouble(value); } catch { percent = 0; }
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            double size = 46;
            if (parameter != null) {
                double p;
                if (double.TryParse(parameter.ToString(), out p)) size = p;
            }

            double center = size / 2.0;
            double radius = (size - Thickness) / 2.0;

            double sweep = percent / 100.0 * 360.0;
            if (sweep <= 0.1) return Geometry.Empty;

            if (sweep >= 359.9)
                return new EllipseGeometry(new Point(center, center), radius, radius);

            double startAngle = 0.0;
            double endAngle = startAngle + sweep;

            Point start = PointOnCircle(center, center, radius, startAngle);
            Point end = PointOnCircle(center, center, radius, endAngle);

            bool isLargeArc = sweep > 180.0;

            PathFigure fig = new PathFigure();
            fig.StartPoint = start;
            fig.IsClosed = false;
            fig.IsFilled = false;

            fig.Segments.Add(new ArcSegment {
                Point = end,
                Size = new Size(radius, radius),
                RotationAngle = 0,
                IsLargeArc = isLargeArc,
                SweepDirection = SweepDirection.Clockwise,
                IsStroked = true
            });

            PathGeometry geo = new PathGeometry();
            geo.Figures.Add(fig);
            return geo;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            return Binding.DoNothing;
        }

        private static Point PointOnCircle(double cx, double cy, double r, double angleDeg) {
            double rad = angleDeg * Math.PI / 180.0;
            return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
        }
    }
}
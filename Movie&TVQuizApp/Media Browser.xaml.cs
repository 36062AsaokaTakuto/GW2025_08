// MediaBrowser.xaml.cs（全体）
// 目的：クイズ検索ボックス表示までは下を押し下げ、候補リストはPopupで重ねて表示（下を押し下げない）

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
using System.Text;
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

        // ===== ★クイズ候補（DBにある「クイズが存在する作品」）=====
        private readonly ObservableCollection<QuizWorkSuggestItem> _quizWorkSuggestions
            = new ObservableCollection<QuizWorkSuggestItem>();
        private CancellationTokenSource _ctsQuizWorkSuggest;

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
            }

            Genres.Clear();
            Genres.Add(new GenreOption { Id = -1, Name = "すべて", IsSelected = true });

            Loaded += MediaBrowser_Loaded;
            SizeChanged += (_, __) => UpdateCardWidth();
            Activated += MediaBrowser_Activated;

            Closing += (_, __) => {
                try { _cts?.Cancel(); } catch { }
                try { _ctsQuizWorkSuggest?.Cancel(); } catch { }
                try { _imgGate.Dispose(); } catch { }
            };

            _menuHideTimer.Interval = TimeSpan.FromMilliseconds(180);
            _menuHideTimer.Tick += (_, __) => {
                _menuHideTimer.Stop();
                HideMenus();
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
            AppNav.GoHome(this);
        }

        // =========================================================
        // 外クリック：クイズ検索のフォーカス制御
        // =========================================================
        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e) {
            DependencyObject src = e.OriginalSource as DependencyObject;

            // TextBox外クリックでフォーカス解除
            if (QuizSearchTextBox != null && QuizSearchTextBox.IsKeyboardFocusWithin) {
                if (src == null || !IsDescendant(src, QuizSearchTextBox)) {
                    Keyboard.ClearFocus();
                }
            }

            // ★候補Popup外クリックで閉じる
            if (QuizWorkSuggestPopup != null && QuizWorkSuggestPopup.IsOpen) {
                bool insideQuizBox = (QuizSearchTextBox != null && src != null && IsDescendant(src, QuizSearchTextBox));
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
        }

        // ★XAMLが呼んでるので必須
        private void QuizSearch_GotFocus(object sender, RoutedEventArgs e) {
            if (QuizSearchTextBox == null) return;

            CancelMenuHide();
            if (QuizMenu != null) QuizMenu.Visibility = Visibility.Visible;
            if (QuizSearchPanel != null) QuizSearchPanel.Visibility = Visibility.Visible;

            QuizSearchTextBox.IsReadOnlyCaretVisible = true;
        }

        // ★XAMLが呼んでるので必須
        private void QuizSearch_LostFocus(object sender, RoutedEventArgs e) {
            if (QuizSearchTextBox == null) return;

            QuizSearchTextBox.IsReadOnlyCaretVisible = false;
            ScheduleMenuHide();
        }

        // ★XAMLが呼んでるので必須
        private void QuizSearch_TextChanged(object sender, TextChangedEventArgs e) {
            if (QuizSearchTextBox == null) return;
            if (QuizSearchTextBox.IsKeyboardFocusWithin) CancelMenuHide();

            // ★候補を更新（ひらがな/カタカナ/大小文字対応）
            StartQuizWorkSuggestDebounce((QuizSearchTextBox.Text ?? "").Trim());
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
            // まずUI（パネル表示＆フォーカス）
            if (QuizSearchPanel != null && QuizSearchPanel.Visibility != Visibility.Visible) {
                ShowQuizSearchPanel();
            }

            if (QuizSearchTextBox != null && !QuizSearchTextBox.IsKeyboardFocusWithin) {
                QuizSearchTextBox.Focus();
            }

            // 入力欄の文字（空なら何もしない）
            string title = (QuizSearchTextBox?.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(title)) return;

            await AppDb.InitAsync();

            // ★完全一致で検索
            Work work = await AppDb.Connection.Table<Work>()
                .Where(w => w.Title == title)
                .FirstOrDefaultAsync();

            if (work == null) return;

            // その作品のクイズ数
            var quizzes = await AppDb.Connection.Table<Quiz>()
                .Where(q => q.WorkKey == work.WorkKey)
                .ToListAsync();

            if (quizzes == null || quizzes.Count == 0) return;

            int firstQuizId = quizzes.OrderBy(q => q.QuizId).First().QuizId;

            // ★候補を閉じる
            HideQuizWorkSuggest();

            // クイズ回答画面へ（全画面）
            var win = new Movie_AnimeQuizApp.Views.QuizPlayWindow(work.WorkKey, firstQuizId);
            win.Owner = this;
            win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            win.WindowState = WindowState.Maximized;

            // メニュー/パネルを閉じる
            if (QuizSearchPanel != null) QuizSearchPanel.Visibility = Visibility.Collapsed;
            if (QuizMenu != null) QuizMenu.Visibility = Visibility.Collapsed;

            this.Hide();
            win.Closed += (_, __) => {
                if (AppNav.ForceMain) return;
                try { this.Show(); this.Activate(); } catch { }
            };

            win.Show();
            win.WindowState = WindowState.Maximized; // ★保険（確実に最大化）
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
                .Where(q => q.WorkKey == work.WorkKey)
                .ToListAsync();

            if (quizzes == null || quizzes.Count <= 0) return;

            int firstQuizId = quizzes.OrderBy(x => Guid.NewGuid()).First().QuizId;

            HideQuizWorkSuggest();

            var win = new Movie_AnimeQuizApp.Views.QuizPlayWindow(work.WorkKey, firstQuizId);
            win.Owner = this;
            win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            win.WindowState = WindowState.Maximized;

            if (QuizSearchPanel != null) QuizSearchPanel.Visibility = Visibility.Collapsed;
            if (QuizMenu != null) QuizMenu.Visibility = Visibility.Collapsed;

            this.Hide();
            win.Closed += (_, __) => {
                if (AppNav.ForceMain) return;
                try { this.Show(); this.Activate(); } catch { }
            };

            win.Show();
            win.WindowState = WindowState.Maximized; // ★保険
        }

        // ★XAMLが呼んでるので必須（クイズ作成）
        private void QuizCreate_Click(object sender, RoutedEventArgs e) {
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

        // =========================================================
        // ★クイズ候補（DB：クイズが存在する作品）表示/取得
        // =========================================================
        private void StartQuizWorkSuggestDebounce(string q) {
            if (string.IsNullOrWhiteSpace(q)) {
                HideQuizWorkSuggest();
                return;
            }

            try { _ctsQuizWorkSuggest?.Cancel(); } catch { }
            _ctsQuizWorkSuggest = new CancellationTokenSource();
            var token = _ctsQuizWorkSuggest.Token;

            _ = Task.Run(async () => {
                try {
                    await Task.Delay(220, token);
                    if (token.IsCancellationRequested) return;

                    var list = await FetchQuizWorkSuggestionsAsync(q, token);
                    if (token.IsCancellationRequested) return;

                    await Dispatcher.InvokeAsync(() => {
                        _quizWorkSuggestions.Clear();
                        for (int i = 0; i < list.Count; i++) _quizWorkSuggestions.Add(list[i]);

                        if (_quizWorkSuggestions.Count > 0) ShowQuizWorkSuggest();
                        else HideQuizWorkSuggest();
                    });
                }
                catch { }
            });
        }

        private async Task<List<QuizWorkSuggestItem>> FetchQuizWorkSuggestionsAsync(string query, CancellationToken token) {
            var ret = new List<QuizWorkSuggestItem>();

            try {
                await AppDb.InitAsync();

                // Quiz側（どの作品にクイズがあるか）
                var quizList = await AppDb.Connection.Table<Quiz>().ToListAsync();
                if (quizList == null || quizList.Count == 0) return ret;

                var hasQuizKeys = new HashSet<string>(
                    quizList.Where(q => q != null && !string.IsNullOrWhiteSpace(q.WorkKey))
                            .Select(q => q.WorkKey)
                );

                if (hasQuizKeys.Count == 0) return ret;

                // Work側（タイトル/ポスター等）
                var works = await AppDb.Connection.Table<Work>().ToListAsync();
                if (works == null || works.Count == 0) return ret;

                string nq = Normalize(query);

                for (int i = 0; i < works.Count; i++) {
                    if (token.IsCancellationRequested) break;

                    var w = works[i];
                    if (w == null) continue;

                    string wk = w.WorkKey ?? "";
                    if (wk.Length == 0) continue;
                    if (!hasQuizKeys.Contains(wk)) continue;

                    string title = w.Title ?? "";
                    if (title.Length == 0) continue;

                    string nt = Normalize(title);
                    if (!(nt.StartsWith(nq) || nt.Contains(nq))) continue;

                    string mt = (w.MediaType ?? "");
                    string dateText = ToJaDate(w.ReleaseDate ?? "");

                    string sub = (mt == "movie" ? "映画" : (mt == "tv" ? "テレビ番組" : ""))
                               + (string.IsNullOrWhiteSpace(dateText) ? "" : " ・ " + dateText);

                    ret.Add(new QuizWorkSuggestItem {
                        WorkKey = wk,
                        Title = title,
                        Sub = sub,
                        PosterThumbUrl = BuildPosterThumbUrlFromStoredPath(w.PosterPath),
                        NormTitle = nt,
                        NormQuery = nq
                    });
                }

                // 表示順：先頭一致→タイトル昇順、最大10件
                ret = ret
                    .OrderByDescending(s => s.NormTitle.StartsWith(s.NormQuery))
                    .ThenBy(s => s.Title, StringComparer.CurrentCulture)
                    .Take(10)
                    .ToList();

                return ret;
            }
            catch {
                return ret;
            }
        }

        private void ShowQuizWorkSuggest() {
            if (QuizWorkSuggestBorder != null) QuizWorkSuggestBorder.Visibility = Visibility.Visible;
            if (QuizWorkSuggestPopup != null) QuizWorkSuggestPopup.IsOpen = true;
        }

        private void HideQuizWorkSuggest() {
            if (QuizWorkSuggestPopup != null) QuizWorkSuggestPopup.IsOpen = false;
            if (QuizWorkSuggestBorder != null) QuizWorkSuggestBorder.Visibility = Visibility.Collapsed;

            if (QuizWorkSuggestList != null) QuizWorkSuggestList.SelectedIndex = -1;
            _quizWorkSuggestions.Clear();
        }

        private void QuizWorkSuggestList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
            if (QuizWorkSuggestList == null) return;

            var it = QuizWorkSuggestList.SelectedItem as QuizWorkSuggestItem;
            if (it == null) return;

            // ★クリックした候補を入れて、そのままクイズ回答へ
            if (QuizSearchTextBox != null) {
                QuizSearchTextBox.Text = it.Title ?? "";
            }

            HideQuizWorkSuggest();

            // 既存の動線（完全一致でQuizPlayへ）
            QuizSearchHit_Click(QuizSearchHit, new RoutedEventArgs());
        }

        private static string BuildPosterThumbUrlFromStoredPath(string posterPathOrUrl) {
            if (string.IsNullOrWhiteSpace(posterPathOrUrl)) return "";
            string p = posterPathOrUrl.Trim();

            if (p.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) {
                return p;
            }

            // Work.PosterPath が "/xxxx.jpg" 形式の想定
            if (!p.StartsWith("/")) p = "/" + p;
            return "https://image.tmdb.org/t/p/w92" + p;
        }

        // ★ひらがな/カタカナ統一 + 半角/全角寄せ + 小文字/大文字無視
        private static string Normalize(string s) {
            if (s == null) return "";
            s = s.Trim();

            // 全角/半角などを寄せる
            s = s.Normalize(NormalizationForm.FormKC);

            // 小文字化（英字）
            s = s.ToLowerInvariant();

            // 空白除去
            s = new string(s.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

            // カタカナ→ひらがな（タイトル側・クエリ側を同じルールにする）
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++) {
                char ch = s[i];

                // 全角カタカナ範囲：ァ(30A1) ～ ヶ(30F6)
                if (ch >= '\u30A1' && ch <= '\u30F6') {
                    ch = (char)(ch - 0x60); // ひらがなへ
                }
                sb.Append(ch);
            }

            return sb.ToString();
        }

        private static string ToJaDate(string raw) {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            DateTime dt;
            if (DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) {
                return dt.ToString("yyyy年M月d日", CultureInfo.GetCultureInfo("ja-JP"));
            }
            return raw;
        }

        // ★候補用Item
        private class QuizWorkSuggestItem {
            public string WorkKey { get; set; }
            public string Title { get; set; }
            public string Sub { get; set; }
            public string PosterThumbUrl { get; set; }

            // ソート用
            public string NormTitle { get; set; }
            public string NormQuery { get; set; }
        }

        // =========================================================
        // メニュークリック：モード切替
        // =========================================================
        private async void MoviePopular_Click(object sender, RoutedEventArgs e) { await SetModeAsync(BrowseMode.MoviePopular); }
        private async void MovieNowPlaying_Click(object sender, RoutedEventArgs e) { await SetModeAsync(BrowseMode.MovieNowPlaying); }
        private async void TvPopular_Click(object sender, RoutedEventArgs e) { await SetModeAsync(BrowseMode.TvPopular); }
        private async void TvOnAir_Click(object sender, RoutedEventArgs e) { await SetModeAsync(BrowseMode.TvOnAir); }

        private async Task SetModeAsync(BrowseMode mode) {
            _mode = mode;
            HideMenus();
            ApplyModeTitle();

            try { await LoadGenresAsync(); } catch { }

            _genreId = -1;
            for (int i = 0; i < Genres.Count; i++)
                Genres[i].IsSelected = (Genres[i].Id == -1);

            await ResetAndLoadAsync();
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

            // 映画の「履歴」だけ消す（元コード踏襲）
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

            _apiPage = 1;
            _apiTotalPages = int.MaxValue;

            _buffer.Clear();
            _bufferIndex = 0;

            _seenKeys.Clear();

            await FillBufferIfNeededAsync(token);
            await AppendNextChunkAsync(token);
        }

        private async void LoadMore_Click(object sender, RoutedEventArgs e) {
            if (_cts == null) return;

            RemoveLoadMoreCard();
            await AppendNextChunkAsync(_cts.Token);
        }

        private void RemoveLoadMoreCard() {
            BrowserRow last = Items.LastOrDefault();
            if (last != null && last.IsLoadMore) Items.Remove(last);
        }

        private void AddLoadMoreCardIfHasMore() {
            BrowserRow last = Items.LastOrDefault();
            if (last != null && last.IsLoadMore) return;

            bool hasMore = (_bufferIndex < _buffer.Count) || (_apiPage <= _apiTotalPages);
            if (hasMore) Items.Add(BrowserRow.LoadMore());
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

            // ★後ろを消さない：Hideしない、モーダルで前に出す
            w.ShowDialog();
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

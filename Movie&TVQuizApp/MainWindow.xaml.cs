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

        // ★クイズ入力開始後は SearchTextBox をクリックするまで「クイズメニュー一式」を閉じない
        private bool _quizSearchPinned = false;

        // ===== 候補 =====
        public ObservableCollection<SuggestItem> Suggestions { get; } = new ObservableCollection<SuggestItem>();
        private CancellationTokenSource _ctsSuggest;

        // ===== メニュー非表示タイマー =====
        private readonly DispatcherTimer _menuHideTimer;

        // ===== 背景タイル=====
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

            // 作品検索は今まで通り（TextBox.Textにプレースホルダーを入れる方式）
            if (SearchTextBox != null && string.IsNullOrWhiteSpace(SearchTextBox.Text)) {
                SearchTextBox.Text = Placeholder;
                SearchTextBox.Foreground = Brushes.Gray;
            }

            // ★クイズ検索は Watermark(TextBlock) 方式なので TextBox.Text は空のまま
            if (QuizSearchTextBox != null) {
                QuizSearchTextBox.Text = "";
                QuizSearchTextBox.Foreground = Brushes.White;

                // 最初はフォーカス外なのでカーソル見せない
                QuizSearchTextBox.IsReadOnlyCaretVisible = false;
            }

            UpdateQuizWatermark();

            _menuHideTimer = new DispatcherTimer();
            _menuHideTimer.Interval = TimeSpan.FromMilliseconds(180);
            _menuHideTimer.Tick += (_, __) => {
                _menuHideTimer.Stop();

                // ★入力開始後は SearchTextBox クリックまで閉じない
                if (IsQuizPinnedOrEditing()) return;

                HideMenus();
            };
        }

        private async void MainWindow_ContentRendered(object sender, EventArgs e) {
            ApplyCanvasTransform();
            await ReloadBackgroundAsync();
        }

        private async void QuizSearchHit_Click(object sender, RoutedEventArgs e) {
            // 入力欄の文字（空なら何もしない）
            string title = (QuizSearchTextBox?.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(title)) return;

            await AppDb.InitAsync();

            // ★完全一致で検索
            Work work = await AppDb.Connection.Table<Work>()
                .Where(w => w.Title == title)
                .FirstOrDefaultAsync();

            if (work == null) {
                return;
            }

            // その作品のクイズ数
            var quizzes = await AppDb.Connection.Table<Quiz>()
                .Where(q => q.WorkKey == work.WorkKey)
                .ToListAsync();

            if (quizzes == null || quizzes.Count == 0) {
                return;
            }

            // QuizPlayWindow を生成（あなたのプロジェクトのコンストラクタ違いに対応）
            int firstQuizId = quizzes.OrderBy(q => q.QuizId).First().QuizId;
            Window quizWin = CreateQuizPlayWindowWindow(work.WorkKey, firstQuizId);

            if (quizWin == null) {
                return;
            }

            // ★クイズがある時だけ画面遷移
            UnpinQuizSearch();
            HideMenus();
            HideSuggest();

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

                // Views配下 or 直下、どちらでも拾う
                var t =
                    asm.GetType("Movie_AnimeQuizApp.Views.QuizPlayWindow", false) ??
                    asm.GetType("Movie_AnimeQuizApp.QuizPlayWindow", false);

                if (t == null) return null;
                if (!typeof(Window).IsAssignableFrom(t)) return null;

                // 1) QuizPlayWindow(string workKey)
                var ctor1 = t.GetConstructor(new Type[] { typeof(string) });
                if (ctor1 != null) {
                    return (Window)ctor1.Invoke(new object[] { workKey });
                }

                // 2) QuizPlayWindow(string workKey, int quizId)
                var ctor2 = t.GetConstructor(new Type[] { typeof(string), typeof(int) });
                if (ctor2 != null) {
                    return (Window)ctor2.Invoke(new object[] { workKey, quizId });
                }

                // それ以外は未対応
                return null;
            }
            catch {
                return null;
            }
        }


        // =========================
        // クリック外で候補/クイズ入力フォーカスを閉じる
        // =========================
        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e) {
            DependencyObject src = e.OriginalSource as DependencyObject;

            // ★SearchTextBoxをクリックしたら「クイズ固定」を解除して、クイズメニューを閉じる
            if (SearchTextBox != null && src != null && IsDescendant(src, SearchTextBox)) {
                UnpinQuizSearch();
                HideMenus(); // ピン解除後なので QuizMenu も閉じる
                return;
            }

            // 候補を閉じる
            if (SuggestBorder != null && SuggestBorder.Visibility == Visibility.Visible) {
                if (src != null) {
                    if (!IsDescendant(src, SuggestBorder) && (SearchTextBox == null || !IsDescendant(src, SearchTextBox))) {
                        HideSuggest();
                    }
                }
            }

            // クイズ検索：テキストボックス以外を押したらフォーカス外してカーソル消す（ただし表示はピン留めで維持）
            if (QuizSearchTextBox != null && QuizSearchTextBox.IsKeyboardFocusWithin) {
                if (src == null || !IsDescendant(src, QuizSearchTextBox)) {
                    Keyboard.ClearFocus(); // LostFocus が走る
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

            if (!string.IsNullOrWhiteSpace(QuizSearchTextBox.Text)) {
                _quizSearchPinned = true;
            }
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
            if (IsQuizPinnedOrEditing()) return; // ★ピン留め中は切替させない
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
            if (IsQuizPinnedOrEditing()) return; // ★ピン留め中は切替させない
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

        // クイズ
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
            // ★ピン留め中は QuizMenu / QuizSearchPanel を維持（他は閉じる）
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
            if (IsQuizPinnedOrEditing()) return; // ★ピン留め中は閉じる予約しない
            _menuHideTimer.Stop();
            _menuHideTimer.Start();
        }

        // =========================
        // クイズ：検索パネル（ホバーで表示）
        // =========================
        private void QuizSearchHit_MouseEnter(object sender, MouseEventArgs e) {
            _menuHideTimer.Stop();
            ShowQuizSearchPanel();
            // ここではフォーカスしない（クリックした時だけカーソル）
        }

        private void QuizSearchHit_MouseLeave(object sender, MouseEventArgs e) {
            _menuHideTimer.Stop();
            _menuHideTimer.Start();
        }

        private void QuizSearchPanel_MouseEnter(object sender, MouseEventArgs e) {
            _menuHideTimer.Stop();
        }

        private void QuizSearchPanel_MouseLeave(object sender, MouseEventArgs e) {
            // ★ピン留め中（または入力中）は消さない
            if (IsQuizPinnedOrEditing()) return;

            _menuHideTimer.Stop();
            _menuHideTimer.Start();
        }

        private void ShowQuizSearchPanel() {
            if (QuizSearchPanel != null) QuizSearchPanel.Visibility = Visibility.Visible;
            UpdateQuizWatermark();
        }

        private void HideQuizSearchPanel() {
            if (IsQuizPinnedOrEditing()) return; // ★
            if (QuizSearchPanel != null) QuizSearchPanel.Visibility = Visibility.Collapsed;
        }

        // クリックした時だけフォーカス（カーソル出す）
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

            // ★ピン留め中は閉じない
            if (IsQuizPinnedOrEditing()) return;

            ScheduleMenuHide();
        }

        // 入力中は消えないように保険 + 1文字でも打ったらピン留め
        private void QuizSearch_TextChanged(object sender, TextChangedEventArgs e) {
            if (QuizSearchTextBox == null) return;

            UpdateQuizWatermark();

            // ★1文字でも入力したら SearchTextBox クリックまで固定
            PinQuizSearchIfNeeded();

            if (QuizSearchTextBox.IsKeyboardFocusWithin) {
                CancelMenuHide();
            }
        }

        private void QuizSearch_PreviewKeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Escape) {
                // ★キャンセル：固定解除して閉じる
                UnpinQuizSearch();
                HideMenus();
                Keyboard.ClearFocus();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter) {
                string q = (QuizSearchTextBox != null ? QuizSearchTextBox.Text : "").Trim();
                if (string.IsNullOrWhiteSpace(q)) return;

                // 遷移するので固定解除
                UnpinQuizSearch();
                HideMenus();
                HideSuggest();

                var win = new SearchResultWindow(q, ApiKey);
                win.Owner = this;

                this.Hide();
                win.Closed += (_, __) => {
                    try { this.Show(); this.Activate(); } catch { }
                };

                win.WindowState = WindowState.Maximized;
                win.Show();

                e.Handled = true;
            }
        }

        // ★SearchTextBoxクリック時：固定解除（XAMLで呼ばれる）
        private void SearchTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
            UnpinQuizSearch();
            HideMenus();
        }

        // クイズ作成
        private void QuizCreate_Click(object sender, RoutedEventArgs e) {
            HideMenus();
            HideSuggest();

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

        // =========================
        // メニュークリック → MediaBrowserへ
        // =========================
        private void MoviePopular_Click(object sender, RoutedEventArgs e) { OpenMediaBrowser(MediaBrowser.BrowseMode.MoviePopular); }
        private void MovieNowPlaying_Click(object sender, RoutedEventArgs e) { OpenMediaBrowser(MediaBrowser.BrowseMode.MovieNowPlaying); }
        private void TvPopular_Click(object sender, RoutedEventArgs e) { OpenMediaBrowser(MediaBrowser.BrowseMode.TvPopular); }
        private void TvOnAir_Click(object sender, RoutedEventArgs e) { OpenMediaBrowser(MediaBrowser.BrowseMode.TvOnAir); }

        private void OpenMediaBrowser(MediaBrowser.BrowseMode mode) {
            UnpinQuizSearch();
            HideMenus();
            HideSuggest();

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
        // 検索 Placeholder
        // =========================
        private void SearchBox_GotFocus(object sender, RoutedEventArgs e) {
            if (SearchTextBox.Text == Placeholder) {
                SearchTextBox.Text = "";
                SearchTextBox.Foreground = Brushes.White;
            }
        }

        private void SearchBox_LostFocus(object sender, RoutedEventArgs e) {
            if (string.IsNullOrWhiteSpace(SearchTextBox.Text)) {
                SearchTextBox.Text = Placeholder;
                SearchTextBox.Foreground = Brushes.Gray;
                HideSuggest();
            }
        }

        // =========================
        // 検索ボタン
        // =========================
        private void Search_Click(object sender, RoutedEventArgs e) {
            string q = (SearchTextBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(q) || q == Placeholder) return;

            // 検索開始なのでクイズ固定解除
            UnpinQuizSearch();
            HideMenus();
            HideSuggest();

            var win = new SearchResultWindow(q, ApiKey);
            win.Owner = this;

            this.Hide();
            win.Closed += (_, __) => {
                try { this.Show(); this.Activate(); } catch { }
            };

            win.WindowState = WindowState.Maximized;
            win.Show();
        }

        // =========================
        // 候補（入力 → 取得）
        // =========================
        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) {
            if (SearchTextBox == null) return;

            string q = (SearchTextBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(q) || q == Placeholder) {
                HideSuggest();
                return;
            }

            if (_ctsSuggest != null) _ctsSuggest.Cancel();
            _ctsSuggest = new CancellationTokenSource();
            var token = _ctsSuggest.Token;

            _ = Task.Run(async () => {
                try {
                    await Task.Delay(240, token);
                    if (token.IsCancellationRequested) return;

                    var list = await FetchSuggestionsAsync(q, token);
                    if (token.IsCancellationRequested) return;

                    await Dispatcher.InvokeAsync(() => {
                        Suggestions.Clear();
                        for (int i = 0; i < list.Length; i++) Suggestions.Add(list[i]);

                        if (Suggestions.Count > 0) ShowSuggest();
                        else HideSuggest();
                    });
                }
                catch { }
            });
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

        private void SuggestList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
            if (SuggestList == null) return;
            var si = SuggestList.SelectedItem as SuggestItem;
            if (si == null) return;
            OpenDetailFromSuggest(si);
        }

        private void OpenDetailFromSuggest(SuggestItem si) {
            // クイズ固定解除
            UnpinQuizSearch();
            HideMenus();
            HideSuggest();

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
            public string MediaType { get; set; } // movie / tv
            public string Title { get; set; }
            public string Sub { get; set; }
            public string PosterThumbUrl { get; set; }
            public string NormTitle { get; set; }
        }

        // =========================
        // 背景：Canvas変形＆タイル
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

        private void QuizDelete_Click(object sender, RoutedEventArgs e) {
            HideMenus();
            HideSuggest();

            var w = new QuizDeleteWindow();
            w.Owner = this;
            w.ShowDialog();
        }
    }
}

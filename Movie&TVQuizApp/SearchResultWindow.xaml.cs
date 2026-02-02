using Microsoft.Web.WebView2.Core;
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
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// ★クイズ起動用
using Movie_AnimeQuizApp.Data;
using Movie_AnimeQuizApp.Data.Entities;

namespace Movie_AnimeQuizApp {
    public partial class SearchResultWindow : Window {

        private static readonly HttpClient _http = new HttpClient();

        private readonly string _apiKey;
        private readonly string _query;
        private readonly bool _useId;

        private int _tmdbId;
        private string _mediaType; // "movie" or "tv"
        private string _trailerKey = "";

        // ===== MediaBrowserと同じ：メニュー非表示タイマー =====
        private readonly DispatcherTimer _menuHideTimer = new DispatcherTimer();

        // ===== ★クイズ検索（DB候補）=====
        private readonly ObservableCollection<QuizWorkSuggestItem> _quizWorkSuggestions = new ObservableCollection<QuizWorkSuggestItem>();
        private CancellationTokenSource _ctsQuizWorkSuggest;

        // ★高速化：クイズがある作品インデックスを一度だけロードして使い回す
        private readonly SemaphoreSlim _quizIndexGate = new SemaphoreSlim(1, 1);
        private List<QuizWorkIndexItem> _quizWorkIndex = null;

        public SearchResultWindow(string query, string apiKey) {
            InitializeComponent();

            _query = query ?? "";
            _apiKey = apiKey ?? "";
            _useId = false;

            PreviewMouseDown += Window_PreviewMouseDown;
            Activated += SearchResultWindow_Activated;

            if (QuizWorkSuggestList != null) {
                QuizWorkSuggestList.ItemsSource = _quizWorkSuggestions;
            }

            InitHeaderMenus();

            Loaded += SearchResultWindow_Loaded;
        }

        public SearchResultWindow(int tmdbId, string mediaType, string apiKey) {
            InitializeComponent();

            _apiKey = apiKey ?? "";
            _useId = true;
            _tmdbId = tmdbId;
            _mediaType = string.IsNullOrWhiteSpace(mediaType) ? "movie" : mediaType;
            _query = "";

            PreviewMouseDown += Window_PreviewMouseDown;
            Activated += SearchResultWindow_Activated;

            if (QuizWorkSuggestList != null) {
                QuizWorkSuggestList.ItemsSource = _quizWorkSuggestions;
            }

            InitHeaderMenus();

            Loaded += SearchResultWindow_Loaded;
        }

        // Home中に“前の詳細ページが復活”したら即消す
        private void SearchResultWindow_Activated(object sender, EventArgs e) {
            if (AppNav.ForceMain) {
                try { Close(); } catch { try { Hide(); } catch { } }
            }
        }

        private void InitHeaderMenus() {
            // クイズ検索：初期は空（プレースホルダー表示）
            if (QuizSearchTextBox != null) {
                QuizSearchTextBox.Text = "";
                QuizSearchTextBox.Foreground = Brushes.White;
                QuizSearchTextBox.IsReadOnlyCaretVisible = false;
            }

            // 候補Popupは閉じておく
            HideQuizWorkSuggest();

            _menuHideTimer.Interval = TimeSpan.FromMilliseconds(180);
            _menuHideTimer.Tick += (_, __) => {
                _menuHideTimer.Stop();
                HideMenus();
            };
        }

        // =========================
        // 外クリック：クイズ検索のフォーカス制御 + 候補Popupを閉じる
        // =========================
        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e) {
            DependencyObject src = e.OriginalSource as DependencyObject;

            // ★候補Popupを閉じる（検索ボックス/Popup以外をクリック）
            if (QuizWorkSuggestPopup != null && QuizWorkSuggestPopup.IsOpen) {
                bool insideBox = (QuizSearchTextBox != null && src != null && IsDescendant(src, QuizSearchTextBox));
                bool insidePopup = (src != null && IsInQuizWorkSuggestArea(src));
                if (!insideBox && !insidePopup) {
                    HideQuizWorkSuggest();
                }
            }

            if (QuizSearchTextBox != null && QuizSearchTextBox.IsKeyboardFocusWithin) {
                if (src == null || !IsDescendant(src, QuizSearchTextBox)) {
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

        // ★候補Popup内クリック判定
        private bool IsInQuizWorkSuggestArea(DependencyObject src) {
            if (src == null) return false;

            if (QuizWorkSuggestList != null && IsDescendant(src, QuizWorkSuggestList))
                return true;

            if (QuizWorkSuggestPopup != null) {
                var child = QuizWorkSuggestPopup.Child as DependencyObject;
                if (child != null && IsDescendant(src, child)) return true;
            }
            return false;
        }

        // =========================
        // ヘッダー/メニュー（ホバー）— MediaBrowserと同じ
        // =========================
        private void MovieHeader_MouseEnter(object sender, MouseEventArgs e) {
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
            if (QuizSearchTextBox != null && QuizSearchTextBox.IsKeyboardFocusWithin) return;
            _menuHideTimer.Stop();
            _menuHideTimer.Start();
        }

        private void HideMenus() {
            if (QuizSearchTextBox != null && QuizSearchTextBox.IsKeyboardFocusWithin) return;

            if (MovieMenu != null) MovieMenu.Visibility = Visibility.Collapsed;
            if (TvMenu != null) TvMenu.Visibility = Visibility.Collapsed;
            if (QuizMenu != null) QuizMenu.Visibility = Visibility.Collapsed;

            HideQuizSearchPanel();
        }

        private void CancelMenuHide() => _menuHideTimer.Stop();

        private void ScheduleMenuHide() {
            _menuHideTimer.Stop();
            _menuHideTimer.Start();
        }

        // =========================
        // クイズ：検索パネル（ホバーで表示）
        // =========================
        private void QuizSearchHit_MouseEnter(object sender, MouseEventArgs e) {
            _menuHideTimer.Stop();
            if (QuizMenu != null) QuizMenu.Visibility = Visibility.Visible;
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
            if (QuizSearchTextBox != null && QuizSearchTextBox.IsKeyboardFocusWithin) return;
            _menuHideTimer.Stop();
            _menuHideTimer.Start();
        }

        private void ShowQuizSearchPanel() {
            if (QuizSearchPanel != null) QuizSearchPanel.Visibility = Visibility.Visible;
        }

        private void HideQuizSearchPanel() {
            if (QuizSearchTextBox != null && QuizSearchTextBox.IsKeyboardFocusWithin) return;
            if (QuizSearchPanel != null) QuizSearchPanel.Visibility = Visibility.Collapsed;

            // ★パネル閉じる時は候補も閉じる
            HideQuizWorkSuggest();
        }

        private void QuizSearch_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
            if (QuizSearchTextBox == null) return;

            _menuHideTimer.Stop();
            if (QuizMenu != null) QuizMenu.Visibility = Visibility.Visible;
            if (QuizSearchPanel != null) QuizSearchPanel.Visibility = Visibility.Visible;

            if (!QuizSearchTextBox.IsKeyboardFocusWithin) {
                e.Handled = true;
                QuizSearchTextBox.Focus();
            }
        }

        private void QuizSearch_GotFocus(object sender, RoutedEventArgs e) {
            if (QuizSearchTextBox == null) return;

            _menuHideTimer.Stop();
            if (QuizMenu != null) QuizMenu.Visibility = Visibility.Visible;
            if (QuizSearchPanel != null) QuizSearchPanel.Visibility = Visibility.Visible;

            QuizSearchTextBox.IsReadOnlyCaretVisible = true;
        }

        private void QuizSearch_LostFocus(object sender, RoutedEventArgs e) {
            if (QuizSearchTextBox == null) return;

            QuizSearchTextBox.IsReadOnlyCaretVisible = false;

            // ★フォーカス先が候補Popup内なら閉じない（候補クリック成立）
            var fe = Keyboard.FocusedElement as DependencyObject;
            if (fe == null || !IsInQuizWorkSuggestArea(fe)) {
                HideQuizWorkSuggest();
            }

            _menuHideTimer.Stop();
            _menuHideTimer.Start();
        }

        private void QuizSearch_TextChanged(object sender, TextChangedEventArgs e) {
            if (QuizSearchTextBox == null) return;
            if (QuizSearchTextBox.IsKeyboardFocusWithin) _menuHideTimer.Stop();

            // ★DB候補更新
            StartQuizWorkSuggestDebounce((QuizSearchTextBox.Text ?? "").Trim());
        }

        private async void QuizSearch_PreviewKeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Escape) {
                HideQuizWorkSuggest();
                Keyboard.ClearFocus();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter) {
                HideQuizWorkSuggest();
                e.Handled = true;
                await TryStartQuizAsync();
            }
        }

        // クイズ回答ボタン
        private async void QuizSearchHit_Click(object sender, RoutedEventArgs e) {
            if (QuizSearchPanel != null && QuizSearchPanel.Visibility != Visibility.Visible) {
                ShowQuizSearchPanel();
            }

            if (QuizSearchTextBox != null && !QuizSearchTextBox.IsKeyboardFocusWithin) {
                QuizSearchTextBox.Focus();
            }

            HideQuizWorkSuggest();
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
                .Where(q => q.WorkKey == work.WorkKey)
                .ToListAsync();

            if (quizzes == null || quizzes.Count <= 0) return;

            int firstQuizId = quizzes.OrderBy(x => Guid.NewGuid()).First().QuizId;

            var win = new Movie_AnimeQuizApp.Views.QuizPlayWindow(work.WorkKey, firstQuizId);
            win.Owner = this;
            win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            win.WindowState = WindowState.Maximized;

            HideQuizWorkSuggest();
            if (QuizSearchPanel != null) QuizSearchPanel.Visibility = Visibility.Collapsed;
            if (QuizMenu != null) QuizMenu.Visibility = Visibility.Collapsed;

            this.Hide();
            win.Closed += (_, __) => {
                if (AppNav.ForceMain) return;
                try { this.Show(); this.Activate(); } catch { }
            };

            win.Show();
            win.WindowState = WindowState.Maximized;
        }

        // =========================
        // ★クイズ候補（DB：クイズがある作品） ※Popup表示
        // =========================
        private void StartQuizWorkSuggestDebounce(string q) {
            if (string.IsNullOrWhiteSpace(q)) {
                HideQuizWorkSuggest();
                return;
            }

            if (_ctsQuizWorkSuggest != null) _ctsQuizWorkSuggest.Cancel();
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

        private async Task EnsureQuizWorkIndexAsync(CancellationToken token) {
            if (_quizWorkIndex != null) return;

            await _quizIndexGate.WaitAsync(token);
            try {
                if (_quizWorkIndex != null) return;

                await AppDb.InitAsync();

                // クイズがある作品だけを一括で取ってインデックス化（1回だけ）
                var rows = await AppDb.Connection.QueryAsync<QuizWorkIndexRow>(
                    "SELECT w.WorkKey as WorkKey, w.Title as Title, w.PosterPath as PosterPath, w.MediaType as MediaType, w.ReleaseDate as ReleaseDate " +
                    "FROM [Work] w " +
                    "WHERE EXISTS (SELECT 1 FROM Quiz q WHERE q.WorkKey = w.WorkKey) " +
                    "ORDER BY w.Title COLLATE NOCASE"
                );

                var list = new List<QuizWorkIndexItem>();
                if (rows != null) {
                    for (int i = 0; i < rows.Count; i++) {
                        var r = rows[i];
                        if (r == null) continue;
                        if (string.IsNullOrWhiteSpace(r.WorkKey)) continue;
                        if (string.IsNullOrWhiteSpace(r.Title)) continue;

                        list.Add(new QuizWorkIndexItem {
                            WorkKey = r.WorkKey,
                            Title = r.Title,
                            PosterPath = r.PosterPath ?? "",
                            MediaType = r.MediaType ?? "",
                            ReleaseDate = r.ReleaseDate ?? "",
                            NormTitle = Normalize(r.Title)
                        });
                    }
                }

                _quizWorkIndex = list;
            }
            catch {
                _quizWorkIndex = new List<QuizWorkIndexItem>();
            }
            finally {
                _quizIndexGate.Release();
            }
        }

        private async Task<List<QuizWorkSuggestItem>> FetchQuizWorkSuggestionsAsync(string query, CancellationToken token) {
            var ret = new List<QuizWorkSuggestItem>();

            try {
                await EnsureQuizWorkIndexAsync(token);
                if (token.IsCancellationRequested) return ret;

                string nq = Normalize(query);

                var hits = new List<QuizWorkSuggestItem>();

                for (int i = 0; i < _quizWorkIndex.Count; i++) {
                    if (token.IsCancellationRequested) break;

                    var it = _quizWorkIndex[i];
                    if (it == null) continue;

                    // ひらがな/カタカナ/大小文字/空白差を吸収
                    if (!(it.NormTitle.StartsWith(nq) || it.NormTitle.Contains(nq))) continue;

                    string mt = it.MediaType ?? "";
                    string dateText = ToJaDate(it.ReleaseDate ?? "");

                    string sub = (mt == "movie" ? "映画" : (mt == "tv" ? "テレビ番組" : ""))
                               + (string.IsNullOrWhiteSpace(dateText) ? "" : " ・ " + dateText);

                    hits.Add(new QuizWorkSuggestItem {
                        WorkKey = it.WorkKey,
                        Title = it.Title,
                        Sub = sub,
                        PosterThumbUrl = BuildPosterThumbUrlFromStoredPath(it.PosterPath),
                        NormTitle = it.NormTitle,
                        NormQuery = nq
                    });
                }

                ret = hits
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
            if (QuizWorkSuggestPopup == null) return;
            if (QuizSearchTextBox == null) return;

            QuizWorkSuggestPopup.PlacementTarget = QuizSearchTextBox;
            QuizWorkSuggestPopup.Placement = PlacementMode.Bottom;

            // テキストボックス基準だと少し右に寄りやすいので、Borderに揃える
            QuizWorkSuggestPopup.HorizontalOffset = -10;
            QuizWorkSuggestPopup.VerticalOffset = 6;

            QuizWorkSuggestPopup.IsOpen = true;
        }

        private void HideQuizWorkSuggest() {
            if (QuizWorkSuggestPopup != null) QuizWorkSuggestPopup.IsOpen = false;
            if (QuizWorkSuggestList != null) QuizWorkSuggestList.SelectedIndex = -1;
            _quizWorkSuggestions.Clear();
        }

        private void QuizWorkSuggestList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
            if (QuizWorkSuggestList == null) return;

            var it = QuizWorkSuggestList.SelectedItem as QuizWorkSuggestItem;
            if (it == null) return;

            if (QuizSearchTextBox != null) {
                QuizSearchTextBox.Text = it.Title ?? "";
            }

            HideQuizWorkSuggest();

            // そのままクイズ開始
            _ = TryStartQuizAsync();
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

        // ★大小文字 + 空白 + 半角ｶﾀｶﾅ + ひら/カタ差を吸収
        private static string Normalize(string s) {
            if (s == null) return "";

            // 1) 前後空白除去
            s = s.Trim();

            // 2) 半角ｶﾀｶﾅ→全角、全角英数など統一（NFKC）
            s = s.Normalize(NormalizationForm.FormKC);

            // 3) 空白除去 + 小文字化 + カタカナ→ひらがな
            var sb = new StringBuilder(s.Length);

            foreach (char ch in s) {
                if (char.IsWhiteSpace(ch)) continue;

                char c = char.ToLowerInvariant(ch);

                // 全角カタカナ(ァ=30A1 ～ ヶ=30F6) → ひらがな(ぁ=3041 ～ ゖ=3096)
                if (c >= '\u30A1' && c <= '\u30F6') {
                    c = (char)(c - 0x60);
                }

                sb.Append(c);
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

        public class QuizWorkSuggestItem {
            public string WorkKey { get; set; }
            public string Title { get; set; }
            public string Sub { get; set; }
            public string PosterThumbUrl { get; set; }

            // ソート用
            public string NormTitle { get; set; }
            public string NormQuery { get; set; }
        }

        private class QuizWorkIndexRow {
            public string WorkKey { get; set; }
            public string Title { get; set; }
            public string PosterPath { get; set; }
            public string MediaType { get; set; }
            public string ReleaseDate { get; set; }
        }

        private class QuizWorkIndexItem {
            public string WorkKey { get; set; }
            public string Title { get; set; }
            public string PosterPath { get; set; }
            public string MediaType { get; set; }
            public string ReleaseDate { get; set; }
            public string NormTitle { get; set; }
        }

        // クイズ作成（MainWindow/MediaBrowserと同じ）
        private void QuizCreate_Click(object sender, RoutedEventArgs e) {
            HideMenus();

            Window w = CreateWindowByTypeNames(new string[] {
                "Movie_AnimeQuizApp.Views.QuizCreateWindow",
                "Movie_AnimeQuizApp.QuizCreateWindow",
                "Movie_AnimeQuizApp.QuizCreate",
                "Movie_AnimeQuizApp.QuizCreatePage"
            });

            if (w == null) {
                MessageBox.Show("クイズ作成画面（QuizCreateWindow）が見つかりません。");
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

        // =========================
        // メニュークリック：MediaBrowserへ
        // =========================
        private void MoviePopular_Click(object sender, RoutedEventArgs e) { OpenMediaBrowser(MediaBrowser.BrowseMode.MoviePopular); }
        private void MovieNowPlaying_Click(object sender, RoutedEventArgs e) { OpenMediaBrowser(MediaBrowser.BrowseMode.MovieNowPlaying); }
        private void TvPopular_Click(object sender, RoutedEventArgs e) { OpenMediaBrowser(MediaBrowser.BrowseMode.TvPopular); }
        private void TvOnAir_Click(object sender, RoutedEventArgs e) { OpenMediaBrowser(MediaBrowser.BrowseMode.TvOnAir); }

        private void OpenMediaBrowser(MediaBrowser.BrowseMode mode) {
            HideMenus();

            var win = new MediaBrowser(mode);
            win.Owner = this;

            this.Hide();
            win.Closed += (_, __) => {
                if (AppNav.ForceMain) return;
                try { this.Show(); this.Activate(); } catch { }
            };

            win.WindowState = WindowState.Maximized;
            win.Show();
        }

        // =========================
        // Home（確実にMainだけにする）
        // =========================
        private void Home_Click(object sender, RoutedEventArgs e) {
            AppNav.GoHome(this);
        }

        // =========================
        // 既存：戻る
        // =========================
        private void BackButton_Click(object sender, RoutedEventArgs e) {
            Close();
        }

        // =========================
        // 既存：ロード
        // =========================
        private async void SearchResultWindow_Loaded(object sender, RoutedEventArgs e) {
            try {
                if (_useId) {
                    await LoadByIdAsync(_tmdbId, _mediaType);
                } else {
                    await LoadByQueryAsync(_query);
                }
            }
            catch (Exception ex) {
                MessageBox.Show("情報取得中にエラー: " + ex.Message);
            }
        }

        private async Task LoadByQueryAsync(string query) {
            if (string.IsNullOrWhiteSpace(query)) return;

            string url =
                "https://api.themoviedb.org/3/search/multi"
                + "?api_key=" + Uri.EscapeDataString(_apiKey)
                + "&language=ja-JP"
                + "&query=" + Uri.EscapeDataString(query)
                + "&include_adult=false";

            string json = await _http.GetStringAsync(url);
            JObject obj = JObject.Parse(json);
            JArray results = obj["results"] as JArray;

            if (results == null || results.Count == 0) {
                MessageBox.Show("作品が見つかりません。");
                return;
            }

            int pickedId = 0;
            string pickedType = "";

            for (int i = 0; i < results.Count; i++) {
                JToken r = results[i];
                string mt = SafeStr(r["media_type"]);
                if (mt != "movie" && mt != "tv") continue;

                int id = r["id"] != null ? (int)r["id"] : 0;
                if (id == 0) continue;

                string poster = SafeStr(r["poster_path"]);
                if (!string.IsNullOrWhiteSpace(poster)) {
                    pickedId = id;
                    pickedType = mt;
                    break;
                }
            }

            if (pickedId == 0) {
                for (int i = 0; i < results.Count; i++) {
                    JToken r = results[i];
                    string mt = SafeStr(r["media_type"]);
                    if (mt != "movie" && mt != "tv") continue;

                    int id = r["id"] != null ? (int)r["id"] : 0;
                    if (id == 0) continue;

                    pickedId = id;
                    pickedType = mt;
                    break;
                }
            }

            if (pickedId == 0) {
                MessageBox.Show("作品IDが取得できませんでした。");
                return;
            }

            await LoadByIdAsync(pickedId, pickedType);
        }

        // ★表示を速くする：テキスト類を先に出して、プロバイダ/出演者/画像等は並列ロード
        private async Task LoadByIdAsync(int tmdbId, string mediaType) {
            if (tmdbId <= 0) return;
            if (string.IsNullOrWhiteSpace(mediaType)) mediaType = "movie";

            _tmdbId = tmdbId;
            _mediaType = mediaType;

            string detailUrl =
                "https://api.themoviedb.org/3/" + _mediaType + "/" + _tmdbId
                + "?api_key=" + Uri.EscapeDataString(_apiKey)
                + "&language=ja-JP";

            string detailJson = await _http.GetStringAsync(detailUrl);
            JObject detailObj = JObject.Parse(detailJson);

            string title = "";
            string overviewJa = SafeStr(detailObj["overview"]);
            string posterPath = SafeStr(detailObj["poster_path"]);
            string backdropPath = SafeStr(detailObj["backdrop_path"]);
            double voteAverage = detailObj["vote_average"] != null ? (double)detailObj["vote_average"] : 0.0;

            string dateRaw = "";
            string dateLabel = "";
            int runtime = 0;

            if (_mediaType == "movie") {
                title = SafeStr(detailObj["title"]);
                if (string.IsNullOrWhiteSpace(title)) title = SafeStr(detailObj["original_title"]);
                dateRaw = SafeStr(detailObj["release_date"]);
                dateLabel = "公開日";
                runtime = detailObj["runtime"] != null ? (int)detailObj["runtime"] : 0;
            } else {
                title = SafeStr(detailObj["name"]);
                if (string.IsNullOrWhiteSpace(title)) title = SafeStr(detailObj["original_name"]);
                dateRaw = SafeStr(detailObj["first_air_date"]);
                dateLabel = "放送開始";
                JArray ert = detailObj["episode_run_time"] as JArray;
                if (ert != null && ert.Count > 0 && ert[0] != null && ert[0].Type == JTokenType.Integer)
                    runtime = ert[0].Value<int>();
            }

            if (string.IsNullOrWhiteSpace(title)) title = "(タイトル不明)";

            List<string> genreNames = new List<string>();
            JToken genres = detailObj["genres"];
            if (genres != null) {
                foreach (JToken g in genres) {
                    string name = SafeStr(g["name"]);
                    if (!string.IsNullOrEmpty(name)) genreNames.Add(name);
                }
            }

            // ===== 先にUIへ反映 =====
            TitleText.Text = title;

            ReleaseDateText.Text = dateLabel + ": " + FormatJapaneseDateOnly(dateRaw);
            GenresText.Text = genreNames.Count > 0 ? string.Join("・", genreNames) : "";
            RuntimeText.Text = runtime > 0 ? (runtime.ToString() + "分") : "";

            OverviewText.Text = overviewJa ?? "";

            double scorePercent = Math.Max(0.0, Math.Min(100.0, voteAverage * 10.0));
            UserScoreText.Text = Math.Round(scorePercent).ToString(CultureInfo.InvariantCulture) + "%";
            UserScoreArc.Data = BuildArcGeometry(70.0, 70.0, 64.0, scorePercent);

            // ★背景(backdrop)：ResourcesのBackdropBrushResへ設定（ここだけ変更）
            var bb = this.Resources["BackdropBrushRes"] as ImageBrush;
            if (bb != null) {
                if (!string.IsNullOrEmpty(backdropPath)) {
                    bb.ImageSource = new BitmapImage(new Uri("https://image.tmdb.org/t/p/original" + backdropPath));
                } else {
                    bb.ImageSource = null;
                }
            }

            // ===== 並列ロード開始 =====
            var trailerTask = ResolveTrailerKeyAsync(_http);
            var providersTask = LoadProvidersAsync(_http);
            var castTask = LoadCastAsync(_http);

            Task posterTask = Task.CompletedTask;
            if (!string.IsNullOrEmpty(posterPath)) {
                string posterUrl = "https://image.tmdb.org/t/p/w500" + posterPath;
                posterTask = SetImageByHttpAsync(_http, PosterImage, posterUrl);
            } else {
                PosterImage.Source = null;
            }

            Task overviewEnTask = Task.CompletedTask;
            if (string.IsNullOrWhiteSpace(overviewJa)) {
                overviewEnTask = LoadEnglishOverviewIfNeededAsync(_http);
            }

            await Task.WhenAll(providersTask, castTask, posterTask, overviewEnTask);

            try {
                _trailerKey = await trailerTask;
            }
            catch {
                _trailerKey = "";
            }
        }

        private async Task LoadEnglishOverviewIfNeededAsync(HttpClient http) {
            try {
                if (!string.IsNullOrWhiteSpace(OverviewText.Text)) return;

                string detailUrlEn =
                    "https://api.themoviedb.org/3/" + _mediaType + "/" + _tmdbId
                    + "?api_key=" + Uri.EscapeDataString(_apiKey)
                    + "&language=en-US";

                string detailJsonEn = await http.GetStringAsync(detailUrlEn);
                JObject detailObjEn = JObject.Parse(detailJsonEn);

                string overviewEn = SafeStr(detailObjEn["overview"]);
                if (!string.IsNullOrWhiteSpace(overviewEn) && string.IsNullOrWhiteSpace(OverviewText.Text)) {
                    OverviewText.Text = overviewEn;
                }
            }
            catch { }
        }

        // 予告：ボタン押下で表示（予告が無い作品は何もしない）
        private async Task ShowTrailerAsync() {
            if (string.IsNullOrEmpty(_trailerKey)) return;

            await EnsureTrailerWebViewAsync();

            TrailerOverlay.Visibility = Visibility.Visible;
            TrailerOverlay.Focus();

            string watchUrl = "https://www.youtube.com/watch?v=" + _trailerKey + "&autoplay=1";
            TrailerWebView.Source = new Uri(watchUrl);
        }

        private async void TrailerButton_Click(object sender, RoutedEventArgs e) {
            await ShowTrailerAsync();
        }

        private async Task<string> ResolveTrailerKeyAsync(HttpClient http) {
            string k = await TryGetTrailerKeyAsync(http, "ja-JP");
            if (!string.IsNullOrEmpty(k)) return k;

            k = await TryGetTrailerKeyAsync(http, "en-US");
            if (!string.IsNullOrEmpty(k)) return k;

            k = await TryGetTrailerKeyAsync(http, null);
            return k ?? "";
        }

        private async Task<string> TryGetTrailerKeyAsync(HttpClient http, string lang) {
            string videosUrl =
                "https://api.themoviedb.org/3/" + _mediaType + "/" + _tmdbId + "/videos"
                + "?api_key=" + Uri.EscapeDataString(_apiKey);

            if (!string.IsNullOrWhiteSpace(lang))
                videosUrl += "&language=" + Uri.EscapeDataString(lang);

            string json = await http.GetStringAsync(videosUrl);
            JObject obj = JObject.Parse(json);

            JToken results = obj["results"];
            if (results == null) return "";

            foreach (JToken v in results) {
                string site = SafeStr(v["site"]);
                string type = SafeStr(v["type"]);
                string key = SafeStr(v["key"]);

                if (site.Equals("YouTube", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(key)) {
                    if (type.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
                        type.Equals("Teaser", StringComparison.OrdinalIgnoreCase)) {
                        return key;
                    }
                }
            }

            foreach (JToken v in results) {
                string site = SafeStr(v["site"]);
                string key = SafeStr(v["key"]);
                if (site.Equals("YouTube", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(key))
                    return key;
            }

            return "";
        }

        // 配信プロバイダ：表示のみ（クリック無し）
        private async Task LoadProvidersAsync(HttpClient http) {
            ProviderIcons.Children.Clear();

            int colsNow = 5;
            try { colsNow = ProviderIcons.Columns > 0 ? ProviderIcons.Columns : 5; }
            catch { }

            int maxIcons = colsNow * 2; // 2行まで
            int added = 0;

            string url =
                "https://api.themoviedb.org/3/" + _mediaType + "/" + _tmdbId + "/watch/providers"
                + "?api_key=" + Uri.EscapeDataString(_apiKey);

            string json = await http.GetStringAsync(url);
            JObject obj = JObject.Parse(json);

            JToken region = obj["results"] != null ? (obj["results"]["JP"] ?? obj["results"]["US"]) : null;
            if (region == null) return;

            JToken list = region["flatrate"] ?? region["rent"] ?? region["buy"] ?? region["free"];
            if (list == null) return;

            foreach (JToken p in list) {
                if (added >= maxIcons) break;

                string providerName = SafeStr(p["provider_name"]);
                string logoPath = SafeStr(p["logo_path"]);
                if (string.IsNullOrEmpty(logoPath)) continue;

                string logoUrl = "https://image.tmdb.org/t/p/w92" + logoPath;

                Image img = new Image {
                    Width = 72,
                    Height = 72,
                    Margin = new Thickness(4, 0, 4, 8),
                    ToolTip = providerName,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Stretch = Stretch.Uniform
                };

                await SetImageByHttpAsync(http, img, logoUrl);
                ProviderIcons.Children.Add(img);
                added++;
            }
        }

        // 出演者
        private async Task LoadCastAsync(HttpClient http) {
            ActorsPanel.Children.Clear();

            JToken cast = null;

            cast = await TryGetCastTokenAsync(http, BuildCreditsUrl(true), "cast");

            if (cast == null || !cast.HasValues) {
                cast = await TryGetCastTokenAsync(http, BuildCreditsUrl(false), "cast");
            }

            if ((cast == null || !cast.HasValues) && _mediaType == "tv") {
                cast = await TryGetCastTokenAsync(http, BuildAggregateCreditsUrl(true), "cast");
                if (cast == null || !cast.HasValues) {
                    cast = await TryGetCastTokenAsync(http, BuildAggregateCreditsUrl(false), "cast");
                }
            }

            if (cast == null || !cast.HasValues) {
                SyncActorsScrollBar();
                return;
            }

            int count = 0;
            foreach (JToken c in cast) {
                if (count >= 16) break;

                string name = SafeStr(c["name"]);
                if (string.IsNullOrEmpty(name)) name = SafeStr(c["original_name"]);
                if (string.IsNullOrEmpty(name)) continue;

                string profilePath = SafeStr(c["profile_path"]);

                StackPanel item = new StackPanel {
                    Orientation = Orientation.Vertical,
                    Width = 120,
                    Margin = new Thickness(12, 0, 12, 0)
                };

                Grid avatarWrap = new Grid { Width = 96, Height = 96 };

                Image avatar = new Image {
                    Width = 96,
                    Height = 96,
                    Stretch = Stretch.UniformToFill
                };

                if (!string.IsNullOrEmpty(profilePath)) {
                    avatar.Source = new BitmapImage(new Uri("https://image.tmdb.org/t/p/w185" + profilePath));
                }

                avatar.Clip = new EllipseGeometry(new Point(48, 48), 48, 48);
                avatarWrap.Children.Add(avatar);

                TextBlock tb = new TextBlock {
                    Text = name,
                    Foreground = Brushes.White,
                    FontSize = 16,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0)
                };

                item.Children.Add(avatarWrap);
                item.Children.Add(tb);

                ActorsPanel.Children.Add(item);
                count++;
            }

            SyncActorsScrollBar();
        }

        private string BuildCreditsUrl(bool withLangJa) {
            string url =
                "https://api.themoviedb.org/3/" + _mediaType + "/" + _tmdbId + "/credits"
                + "?api_key=" + Uri.EscapeDataString(_apiKey);

            if (withLangJa) url += "&language=ja-JP";
            return url;
        }

        private string BuildAggregateCreditsUrl(bool withLangJa) {
            string url =
                "https://api.themoviedb.org/3/tv/" + _tmdbId + "/aggregate_credits"
                + "?api_key=" + Uri.EscapeDataString(_apiKey);

            if (withLangJa) url += "&language=ja-JP";
            return url;
        }

        private async Task<JToken> TryGetCastTokenAsync(HttpClient http, string url, string tokenName) {
            try {
                string json = await http.GetStringAsync(url);
                JObject obj = JObject.Parse(json);
                return obj[tokenName];
            }
            catch {
                return null;
            }
        }

        private void SyncActorsScrollBar() {
            try {
                ActorsScroll.ScrollToHorizontalOffset(0);
                ActorsHScroll.Value = 0;
                ActorsHScroll.Maximum = ActorsScroll.ScrollableWidth;
                ActorsHScroll.ViewportSize = ActorsScroll.ViewportWidth;
            }
            catch { }
        }

        private bool _syncingActorsScroll = false;

        private void ActorsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) {
            if (_syncingActorsScroll) return;

            _syncingActorsScroll = true;
            try {
                ActorsHScroll.Maximum = ActorsScroll.ScrollableWidth;
                ActorsHScroll.ViewportSize = ActorsScroll.ViewportWidth;
                ActorsHScroll.Value = ActorsScroll.HorizontalOffset;
            }
            finally {
                _syncingActorsScroll = false;
            }
        }

        private void ActorsHScroll_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
            if (_syncingActorsScroll) return;

            _syncingActorsScroll = true;
            try {
                ActorsScroll.ScrollToHorizontalOffset(e.NewValue);
            }
            finally {
                _syncingActorsScroll = false;
            }
        }

        private void CloseTrailer_Click(object sender, RoutedEventArgs e) { CloseTrailerOverlay(); }

        private void TrailerOverlay_KeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Escape) CloseTrailerOverlay();
        }

        private void CloseTrailerOverlay() {
            try {
                if (TrailerWebView != null)
                    TrailerWebView.Source = new Uri("about:blank");
            }
            catch { }

            TrailerOverlay.Visibility = Visibility.Collapsed;
        }

        private async Task EnsureTrailerWebViewAsync() {
            if (TrailerWebView == null) return;

            if (TrailerWebView.CoreWebView2 == null) {
                await TrailerWebView.EnsureCoreWebView2Async();

                TrailerWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                TrailerWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                TrailerWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;

                TrailerWebView.CoreWebView2.NavigationStarting += (s, ev) => {
                    if (string.IsNullOrEmpty(ev.Uri)) return;

                    if (IsAppScheme(ev.Uri)) {
                        ev.Cancel = true;
                        return;
                    }

                    if (ev.Uri.IndexOf("youtube.com/embed/", StringComparison.OrdinalIgnoreCase) >= 0) {
                        ev.Cancel = true;
                        return;
                    }
                };
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

        private Geometry BuildArcGeometry(double cx, double cy, double radius, double percent) {
            double p = Math.Max(0.0, Math.Min(100.0, percent));
            if (p <= 0.0) return Geometry.Empty;
            if (p >= 100.0) return new EllipseGeometry(new Point(cx, cy), radius, radius);

            double angle = 360.0 * (p / 100.0);
            double rad = (Math.PI / 180.0) * angle;

            Point start = new Point(cx + radius, cy);
            Point end = new Point(
                cx + radius * Math.Cos(rad),
                cy + radius * Math.Sin(rad)
            );

            bool isLargeArc = angle > 180.0;

            PathFigure figure = new PathFigure();
            figure.StartPoint = start;
            figure.Segments.Add(new ArcSegment {
                Point = end,
                Size = new Size(radius, radius),
                IsLargeArc = isLargeArc,
                SweepDirection = SweepDirection.Clockwise
            });

            PathGeometry geom = new PathGeometry();
            geom.Figures.Add(figure);
            return geom;
        }

        private string SafeStr(JToken token) { return token == null ? "" : token.ToString(); }

        private string FormatJapaneseDateOnly(string raw) {
            if (string.IsNullOrEmpty(raw)) return "-";
            DateTime dt;
            if (DateTime.TryParse(raw, out dt))
                return dt.ToString("yyyy年M月d日", CultureInfo.GetCultureInfo("ja-JP"));
            return raw;
        }

        private async Task SetImageByHttpAsync(HttpClient http, Image target, string url) {
            try {
                byte[] bytes = await http.GetByteArrayAsync(url);
                using (var ms = new MemoryStream(bytes)) {
                    BitmapImage bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.StreamSource = ms;
                    bi.EndInit();
                    bi.Freeze();
                    target.Source = bi;
                }
            }
            catch {
                target.Source = null;
            }
        }

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

            // ★この画面は消さない
            w.ShowDialog();
        }
    }
}

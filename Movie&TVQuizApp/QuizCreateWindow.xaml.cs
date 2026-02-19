// QuizCreateWindow.xaml.cs（全体）
using Movie_AnimeQuizApp.Data;
using Movie_AnimeQuizApp.Data.Entities;
using Movie_AnimeQuizApp.Share;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.ComponentModel; // ★追加：CancelEventArgs
using System.Windows.Threading; // ★追加：DispatcherPriority

namespace Movie_AnimeQuizApp.Views {
    public partial class QuizCreateWindow : Window {
        private const string ApiKey = "0fa85086e0e7e8c979d1ff066b894bf5";
        private const string Placeholder = "クイズを作りたい作品名を入力";
        private const string QuestionPlaceholder = "問題文を入力してください。";

        private const string TmdbPosterThumbBase = "https://image.tmdb.org/t/p/w92";
        private const string TmdbPosterBase = "https://image.tmdb.org/t/p/w342";
        private const string TmdbBackdropBase = "https://image.tmdb.org/t/p/w1280";

        private static readonly HttpClient _http = new HttpClient();

        private readonly ObservableCollection<TmdbSuggestItem> _workSuggestions = new ObservableCollection<TmdbSuggestItem>();
        private Work _selectedWork;

        private bool _uiReady;
        private CancellationTokenSource _searchCts;
        private CancellationTokenSource _imageCts;

        private const string OverviewInstruction = "〇になっている単語を選択肢から選べ。";
        private static readonly string[] OverviewBlockPhrases = new[] {
            "概要がまだ翻訳されていません",
            "翻訳版を入力してデータベースを一緒に充実させましょう"
        };

        // =====================================================
        // ★追加：ひら/カタ、半角/全角、英字大小を無視して比較
        // =====================================================
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

            Add(nfkc);
            Add(ToHiragana(nfkc));
            Add(ToKatakana(nfkc));

            return list;
        }

        // =====================================================
        // ★追加：Close時にOwnerが出る前の「一瞬の隙間」を作らない
        // =====================================================
        private bool _closingGuard = false;

        public QuizCreateWindow() {
            InitializeComponent();

            WorkSuggestList.ItemsSource = _workSuggestions;

            CorrectRadio1.IsChecked = true;
            UpdateCorrectRadioLabels();

            WorkSearchTextBox.Text = Placeholder;
            WorkSearchTextBox.Foreground = Brushes.Gray;

            QuestionTextBox.Text = QuestionPlaceholder;
            QuestionTextBox.Foreground = Brushes.Gray;

            QuestionTextBox.GotFocus += Question_GotFocus;
            QuestionTextBox.LostFocus += Question_LostFocus;

            // ★追加：閉じる時のデスクトップちら見え防止
            Closing += QuizCreateWindow_Closing;
        }

        private void QuizCreateWindow_Closing(object sender, CancelEventArgs e) {
            if (_closingGuard) return;

            _closingGuard = true;

            try {
                if (Owner != null) {
                    if (!Owner.IsVisible) {
                        Owner.Show();
                    }
                    try {
                        if (Owner.WindowState == WindowState.Minimized) {
                            Owner.WindowState = WindowState.Maximized;
                        }
                    }
                    catch { }
                    try { Owner.Activate(); } catch { }
                }
            }
            catch { }

            e.Cancel = true;

            Dispatcher.BeginInvoke(new Action(() => {
                try {
                    Closing -= QuizCreateWindow_Closing;
                    try { Close(); } catch { }
                }
                catch { }
            }), DispatcherPriority.Render);
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e) {

            // ★追加：150%表示でも作業領域(タスクバー除く)にぴったり全画面化（全体スクロール不要にするため）
            var wa = SystemParameters.WorkArea;
            Left = wa.Left;
            Top = wa.Top;
            Width = wa.Width;
            Height = wa.Height;

            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

            try {
                if (!_http.DefaultRequestHeaders.UserAgent.Any()) {
                    _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
                }
            }
            catch { }

            await AppDb.InitAsync();

            // ★デフォルト文字（infosys等）が入らないように空にする
            CreatedByTextBox.Text = "";

            ApplyWorkToUI(null);

            WorkSearchTextBox.TextChanged += WorkSearchTextBox_TextChanged;
            _uiReady = true;
        }

        private void WorkSearch_GotFocus(object sender, RoutedEventArgs e) {
            if (WorkSearchTextBox.Text == Placeholder) {
                WorkSearchTextBox.Text = "";
                WorkSearchTextBox.Foreground = Brushes.White;
            }
        }

        private void WorkSearch_LostFocus(object sender, RoutedEventArgs e) {
            if (string.IsNullOrWhiteSpace(WorkSearchTextBox.Text)) {
                WorkSearchTextBox.Text = Placeholder;
                WorkSearchTextBox.Foreground = Brushes.Gray;
            }
        }

        private void Question_GotFocus(object sender, RoutedEventArgs e) {
            if (QuestionTextBox.Text == QuestionPlaceholder) {
                QuestionTextBox.Text = "";
                QuestionTextBox.Foreground = Brushes.White;
            }
        }

        private void Question_LostFocus(object sender, RoutedEventArgs e) {
            if (string.IsNullOrWhiteSpace(QuestionTextBox.Text)) {
                QuestionTextBox.Text = QuestionPlaceholder;
                QuestionTextBox.Foreground = Brushes.Gray;
            }
        }

        private async void WorkSearchTextBox_TextChanged(object sender, TextChangedEventArgs e) {
            if (!_uiReady) return;
            if (WorkSuggestPopup == null) return;

            string q = (WorkSearchTextBox.Text ?? "").Trim();
            if (q.Length == 0 || q == Placeholder) {
                _workSuggestions.Clear();
                WorkSuggestPopup.IsOpen = false;
                return;
            }

            if (_searchCts != null) _searchCts.Cancel();
            _searchCts = new CancellationTokenSource();
            CancellationToken token = _searchCts.Token;

            try {
                await Task.Delay(180, token);

                TmdbSuggestItem[] items = await SearchTmdbAsync(q, token);

                _workSuggestions.Clear();
                for (int i = 0; i < items.Length; i++) _workSuggestions.Add(items[i]);

                WorkSuggestPopup.IsOpen = _workSuggestions.Count > 0;
            }
            catch (OperationCanceledException) { }
            catch {
                _workSuggestions.Clear();
                WorkSuggestPopup.IsOpen = false;
            }
        }

        private async Task<TmdbSuggestItem[]> SearchTmdbAsync(string query, CancellationToken token) {
            var variants = BuildQueryVariants(query);
            if (variants.Count == 0) return Array.Empty<TmdbSuggestItem>();

            var map = new Dictionary<string, TmdbSuggestItem>();

            for (int i = 0; i < variants.Count; i++) {
                if (token.IsCancellationRequested) break;

                var part = await SearchTmdbOnceAsync(variants[i], token);
                if (token.IsCancellationRequested) break;

                for (int j = 0; j < part.Length; j++) {
                    var x = part[j];
                    string key = (x.MediaType ?? "") + ":" + x.TmdbId.ToString();
                    if (!map.ContainsKey(key)) map[key] = x;
                }
            }

            var merged = map.Values
                .Where(x => !string.IsNullOrWhiteSpace(x.Title))
                .Where(x => JaContains(x.Title, query))
                .OrderByDescending(x => JaStartsWith(x.Title, query))
                .ThenBy(x => x.Title, StringComparer.CurrentCulture)
                .Take(50)
                .ToArray();

            return merged;
        }

        private async Task<TmdbSuggestItem[]> SearchTmdbOnceAsync(string query, CancellationToken token) {
            string url =
                "https://api.themoviedb.org/3/search/multi" +
                "?api_key=" + ApiKey +
                "&language=ja-JP" +
                "&include_adult=false" +
                "&query=" + Uri.EscapeDataString(query);

            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url))
            using (HttpResponseMessage res = await _http.SendAsync(req, token)) {
                if (!res.IsSuccessStatusCode) {
                    string body = await res.Content.ReadAsStringAsync();
                    throw new InvalidOperationException("HTTP " + (int)res.StatusCode + " / " + body);
                }

                string json = await res.Content.ReadAsStringAsync();
                JObject root = JObject.Parse(json);

                var results = root["results"]
                    .Where(r => r != null)
                    .Where(r => {
                        string mt = (string)r["media_type"];
                        return mt == "movie" || mt == "tv";
                    })
                    .Select(r => {
                        string mediaType = (string)r["media_type"];
                        int id = (int)r["id"];
                        string title = mediaType == "movie" ? (string)r["title"] : (string)r["name"];
                        string date = mediaType == "movie" ? (string)r["release_date"] : (string)r["first_air_date"];
                        string posterPath = (string)r["poster_path"];

                        string thumb = string.IsNullOrWhiteSpace(posterPath) ? "" : (TmdbPosterThumbBase + posterPath);
                        string sub = (string.IsNullOrWhiteSpace(date) ? "" : date) + "  (" + mediaType + ")";

                        return new TmdbSuggestItem {
                            MediaType = mediaType,
                            TmdbId = id,
                            Title = title ?? "",
                            PosterThumbUrl = thumb,
                            Sub = sub
                        };
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Title))
                    .ToArray();

                return results;
            }
        }

        private void WorkSearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Down) {
                if (_workSuggestions.Count > 0) {
                    WorkSuggestList.Focus();
                    WorkSuggestList.SelectedIndex = 0;
                }
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter) {
                if (_workSuggestions.Count > 0) SelectWork(_workSuggestions[0]);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape) {
                if (WorkSuggestPopup != null) WorkSuggestPopup.IsOpen = false;
                e.Handled = true;
            }
        }

        private void WorkSuggestList_PreviewKeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Enter) {
                TmdbSuggestItem it = WorkSuggestList.SelectedItem as TmdbSuggestItem;
                if (it != null) SelectWork(it);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape) {
                if (WorkSuggestPopup != null) WorkSuggestPopup.IsOpen = false;
                WorkSearchTextBox.Focus();
                e.Handled = true;
            }
        }

        private void WorkSuggestList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
            TmdbSuggestItem it = WorkSuggestList.SelectedItem as TmdbSuggestItem;
            if (it != null) SelectWork(it);
        }

        private async void SelectWork(TmdbSuggestItem it) {
            if (it == null) return;

            if (_searchCts != null) _searchCts.Cancel();
            _searchCts = new CancellationTokenSource();
            CancellationToken token = _searchCts.Token;

            try {
                Work work = await FetchWorkDetailsFromTmdbAsync(it.MediaType, it.TmdbId, token);
                await AppDb.Connection.InsertOrReplaceAsync(work);

                _selectedWork = work;

                WorkSearchTextBox.TextChanged -= WorkSearchTextBox_TextChanged;
                WorkSearchTextBox.Text = work.Title ?? "";
                WorkSearchTextBox.Foreground = Brushes.White;
                WorkSearchTextBox.TextChanged += WorkSearchTextBox_TextChanged;

                _workSuggestions.Clear();
                if (WorkSuggestPopup != null) WorkSuggestPopup.IsOpen = false;

                ApplyWorkToUI(work);

                // ★概要は入れない
                // ApplyOverviewToQuestionBox(work);
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private async Task<Work> FetchWorkDetailsFromTmdbAsync(string mediaType, int tmdbId, CancellationToken token) {
            string endpoint = mediaType == "tv" ? "tv" : "movie";

            string url =
                "https://api.themoviedb.org/3/" + endpoint + "/" + tmdbId +
                "?api_key=" + ApiKey +
                "&language=ja-JP";

            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url))
            using (HttpResponseMessage res = await _http.SendAsync(req, token)) {
                if (!res.IsSuccessStatusCode) {
                    string body = await res.Content.ReadAsStringAsync();
                    throw new InvalidOperationException("HTTP " + (int)res.StatusCode + " / " + body);
                }

                string json = await res.Content.ReadAsStringAsync();
                JObject root = JObject.Parse(json);

                string title = endpoint == "movie" ? (string)root["title"] : (string)root["name"];
                string overview = (string)root["overview"];
                string posterPath = (string)root["poster_path"];
                string backdropPath = (string)root["backdrop_path"];
                string releaseDate = endpoint == "movie" ? (string)root["release_date"] : (string)root["first_air_date"];

                return new Work {
                    WorkKey = endpoint + ":" + tmdbId,
                    TmdbId = tmdbId,
                    MediaType = endpoint,
                    Title = title ?? "",
                    Overview = overview ?? "",
                    PosterPath = posterPath ?? "",
                    BackdropPath = backdropPath ?? "",
                    ReleaseDate = releaseDate ?? ""
                };
            }
        }

        private async void ApplyWorkToUI(Work w) {
            if (w == null) {
                try { WorkTitleText.Text = "未選択"; } catch { }
                try { BackdropImage.Source = null; } catch { }
                return;
            }

            try { WorkTitleText.Text = w.Title ?? ""; } catch { }
            try { BackdropImage.Source = null; } catch { }

            if (_imageCts != null) _imageCts.Cancel();
            _imageCts = new CancellationTokenSource();
            CancellationToken token = _imageCts.Token;

            string posterUrl = BuildTmdbUrl(TmdbPosterBase, w.PosterPath);
            string backdropUrl = BuildTmdbUrl(TmdbBackdropBase, w.BackdropPath);

            try {
                string bgUrl = !string.IsNullOrWhiteSpace(backdropUrl)
                    ? backdropUrl
                    : BuildTmdbUrl(TmdbBackdropBase, w.PosterPath);

                if (!string.IsNullOrWhiteSpace(bgUrl)) {
                    BackdropImage.Source = await DownloadBitmapAsync(bgUrl, token);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private string BuildTmdbUrl(string baseUrl, string path) {
            if (string.IsNullOrWhiteSpace(path)) return "";
            if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return path;
            return baseUrl + path;
        }

        private async Task<BitmapImage> DownloadBitmapAsync(string url, CancellationToken token) {
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url))
            using (HttpResponseMessage res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token)) {
                res.EnsureSuccessStatusCode();

                byte[] bytes = await res.Content.ReadAsByteArrayAsync();

                BitmapImage bmp = new BitmapImage();
                using (MemoryStream ms = new MemoryStream(bytes)) {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                }
                return bmp;
            }
        }

        private void UpdateCorrectRadioLabels() {
            if (CorrectRadio1 != null) CorrectRadio1.Content = (CorrectRadio1.IsChecked == true) ? "正解" : "不正解";
            if (CorrectRadio2 != null) CorrectRadio2.Content = (CorrectRadio2.IsChecked == true) ? "正解" : "不正解";
            if (CorrectRadio3 != null) CorrectRadio3.Content = (CorrectRadio3.IsChecked == true) ? "正解" : "不正解";
        }

        private void CorrectRadio_Checked(object sender, RoutedEventArgs e) {
            if (CorrectRadio1.IsChecked != true &&
                CorrectRadio2.IsChecked != true &&
                CorrectRadio3.IsChecked != true) {
                CorrectRadio1.IsChecked = true;
            }

            UpdateCorrectRadioLabels();
        }

        // ★入力中に外側ScrollViewerが勝手に動くのを防ぐ（見た目は変えない）
        private void QuestionTextBox_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e) {
            if (QuestionTextBox != null && QuestionTextBox.IsKeyboardFocusWithin) {
                e.Handled = true;
            }
        }

        // ★ホイールで外側へ伝播しない（問題文だけスクロール）
        private void QuestionTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e) {
            if (QuestionTextBox == null) return;

            if (e.Delta > 0) QuestionTextBox.LineUp();
            else QuestionTextBox.LineDown();

            e.Handled = true;
        }

        private async void Save_Click(object sender, RoutedEventArgs e) {
            await AppDb.InitAsync();

            if (_selectedWork == null) { return; }

            // ★作成者未入力は保存できない
            string createdBy = (CreatedByTextBox.Text ?? "").Trim();
            if (createdBy.Length == 0) {
                return;
            }

            string question = (QuestionTextBox.Text ?? "").Trim();
            if (question.Length == 0 || question == QuestionPlaceholder) {
                return;
            }

            string c1 = (ChoiceTextBox1.Text ?? "").Trim();
            string c2 = (ChoiceTextBox2.Text ?? "").Trim();
            string c3 = (ChoiceTextBox3.Text ?? "").Trim();

            if (c1.Length == 0 || c2.Length == 0 || c3.Length == 0) {
                return;
            }

            int correctIndex =
                CorrectRadio1.IsChecked == true ? 1 :
                CorrectRadio2.IsChecked == true ? 2 :
                CorrectRadio3.IsChecked == true ? 3 : 0;

            if (correctIndex == 0) { MessageBox.Show("正解を1つ選択してください。"); return; }

            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            try {
                await AppDb.Connection.InsertOrReplaceAsync(_selectedWork);

                Quiz quiz = new Quiz {
                    WorkKey = _selectedWork.WorkKey,
                    Type = 1,
                    Question = question,
                    CreatedBy = createdBy,
                    CreatedAt = now
                };

                await AppDb.Connection.InsertAsync(quiz);

                if (quiz.QuizId <= 0) {
                    quiz.QuizId = await AppDb.Connection.ExecuteScalarAsync<int>("select last_insert_rowid()");
                }

                Choice ch1 = new Choice { QuizId = quiz.QuizId, Text = c1, IsCorrect = (correctIndex == 1) };
                Choice ch2 = new Choice { QuizId = quiz.QuizId, Text = c2, IsCorrect = (correctIndex == 2) };
                Choice ch3 = new Choice { QuizId = quiz.QuizId, Text = c3, IsCorrect = (correctIndex == 3) };

                await AppDb.Connection.InsertAsync(ch1);
                await AppDb.Connection.InsertAsync(ch2);
                await AppDb.Connection.InsertAsync(ch3);

                await QuizShare.AppendAsync(
                    _selectedWork,
                    quiz,
                    new List<Choice> {
                        new Choice { Text = c1, IsCorrect = (correctIndex == 1) },
                        new Choice { Text = c2, IsCorrect = (correctIndex == 2) },
                        new Choice { Text = c3, IsCorrect = (correctIndex == 3) },
                    }
                );

                Close();
            }
            catch (Exception ex) {
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) {
            Close();
        }

        public class TmdbSuggestItem {
            public string MediaType { get; set; }
            public int TmdbId { get; set; }
            public string Title { get; set; }
            public string PosterThumbUrl { get; set; }
            public string Sub { get; set; }
        }

        private void ApplyOverviewToQuestionBox(Work work) {
            if (QuestionTextBox == null) return;

            string ov = (work?.Overview ?? "").Trim();

            if (ov.Length == 0) {
                QuestionTextBox.Text = OverviewInstruction + Environment.NewLine;
                QuestionTextBox.Foreground = Brushes.White;
                return;
            }

            for (int i = 0; i < OverviewBlockPhrases.Length; i++) {
                if (ov.Contains(OverviewBlockPhrases[i])) {
                    QuestionTextBox.Text = OverviewInstruction + Environment.NewLine;
                    QuestionTextBox.Foreground = Brushes.White;
                    return;
                }
            }

            bool hasJa = ov.Any(ch =>
                (ch >= '\u3040' && ch <= '\u309F') ||
                (ch >= '\u30A0' && ch <= '\u30FF') ||
                (ch >= '\u4E00' && ch <= '\u9FFF')
            );

            if (!hasJa) {
                QuestionTextBox.Text = OverviewInstruction + Environment.NewLine;
                QuestionTextBox.Foreground = Brushes.White;
                return;
            }

            QuestionTextBox.Text = OverviewInstruction + Environment.NewLine + ov;
            QuestionTextBox.Foreground = Brushes.White;
        }
    }
}

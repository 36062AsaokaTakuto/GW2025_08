// QuizCreateWindow.xaml.cs（修正後・全体）
// ※XAMLは変更なし（Save_ClickだけDB保存が確実になるように調整）
// ※QuestionPlaceholder は「問題文を入力してください。」のプレースホルダー用
using Movie_AnimeQuizApp.Data;
using Movie_AnimeQuizApp.Data.Entities;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
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

namespace Movie_AnimeQuizApp.Views {
    public partial class QuizCreateWindow : Window {

        private const string ApiKey = "0fa85086e0e7e8c979d1ff066b894bf5";
        private const string Placeholder = "クイズを作りたい作品を検索";
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

        public QuizCreateWindow() {
            InitializeComponent();

            WorkSuggestList.ItemsSource = _workSuggestions;
            CorrectRadio1.IsChecked = true;

            WorkSearchTextBox.Text = Placeholder;
            WorkSearchTextBox.Foreground = Brushes.Gray;

            // ★問題文プレースホルダー初期表示
            QuestionTextBox.Text = QuestionPlaceholder;
            QuestionTextBox.Foreground = Brushes.Gray;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e) {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

            try {
                if (!_http.DefaultRequestHeaders.UserAgent.Any()) {
                    _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
                }
            }
            catch { }

            await AppDb.InitAsync();

            CreatedByTextBox.Text = Environment.UserName;
            try { HintText.Text = "作品を検索して選択してください（タイプは中身=1固定）"; } catch { }

            ApplyWorkToUI(null);

            WorkSearchTextBox.TextChanged += WorkSearchTextBox_TextChanged;
            _uiReady = true;
        }

        // -------------------------
        // 作品検索：プレースホルダー
        // -------------------------
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

        // -------------------------
        // ★問題文：プレースホルダー
        // -------------------------
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

        // -------------------------
        // 作品検索：TextChanged
        // -------------------------
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

                var items = await SearchTmdbAsync(q, token);

                _workSuggestions.Clear();
                foreach (var it in items) _workSuggestions.Add(it);

                WorkSuggestPopup.IsOpen = _workSuggestions.Count > 0;
            }
            catch (OperationCanceledException) {
            }
            catch (Exception ex) {
                _workSuggestions.Clear();
                WorkSuggestPopup.IsOpen = false;
                try { HintText.Text = "TMDB検索エラー: " + ex.Message; } catch { }
            }
        }

        private async Task<TmdbSuggestItem[]> SearchTmdbAsync(string query, CancellationToken token) {
            string url =
                "https://api.themoviedb.org/3/search/multi" +
                "?api_key=" + ApiKey +
                "&language=ja-JP" +
                "&include_adult=false" +
                "&query=" + Uri.EscapeDataString(query);

            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            using (var res = await _http.SendAsync(req, token)) {
                if (!res.IsSuccessStatusCode) {
                    string body = await res.Content.ReadAsStringAsync();
                    throw new InvalidOperationException("HTTP " + (int)res.StatusCode + " " + res.ReasonPhrase + " / " + body);
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

                return results
                    .OrderBy(x => x.Title.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
                    .ThenBy(x => x.Title)
                    .Take(50)
                    .ToArray();
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
                var it = WorkSuggestList.SelectedItem as TmdbSuggestItem;
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
            var it = WorkSuggestList.SelectedItem as TmdbSuggestItem;
            if (it != null) SelectWork(it);
        }

        private async void SelectWork(TmdbSuggestItem it) {
            if (it == null) return;

            if (_searchCts != null) _searchCts.Cancel();
            _searchCts = new CancellationTokenSource();
            CancellationToken token = _searchCts.Token;

            try {
                var work = await FetchWorkDetailsFromTmdbAsync(it.MediaType, it.TmdbId, token);
                await AppDb.Connection.InsertOrReplaceAsync(work);

                _selectedWork = work;

                WorkSearchTextBox.TextChanged -= WorkSearchTextBox_TextChanged;
                WorkSearchTextBox.Text = work.Title ?? "";
                WorkSearchTextBox.Foreground = Brushes.White;
                WorkSearchTextBox.TextChanged += WorkSearchTextBox_TextChanged;

                _workSuggestions.Clear();
                if (WorkSuggestPopup != null) WorkSuggestPopup.IsOpen = false;

                ApplyWorkToUI(work);
                try { HintText.Text = ""; } catch { }
            }
            catch (OperationCanceledException) {
            }
            catch (Exception ex) {
                try { HintText.Text = "詳細取得エラー: " + ex.Message; } catch { }
            }
        }

        private async Task<Work> FetchWorkDetailsFromTmdbAsync(string mediaType, int tmdbId, CancellationToken token) {
            string endpoint = mediaType == "tv" ? "tv" : "movie";

            string url =
                "https://api.themoviedb.org/3/" + endpoint + "/" + tmdbId +
                "?api_key=" + ApiKey +
                "&language=ja-JP";

            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            using (var res = await _http.SendAsync(req, token)) {
                if (!res.IsSuccessStatusCode) {
                    string body = await res.Content.ReadAsStringAsync();
                    throw new InvalidOperationException("HTTP " + (int)res.StatusCode + " " + res.ReasonPhrase + " / " + body);
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
            catch (OperationCanceledException) {
            }
            catch (Exception ex) {
                try { HintText.Text = "画像読み込み失敗: " + ex.Message; } catch { }
            }
        }

        private string BuildTmdbUrl(string baseUrl, string path) {
            if (string.IsNullOrWhiteSpace(path)) return "";
            if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return path;
            return baseUrl + path;
        }

        private async Task<BitmapImage> DownloadBitmapAsync(string url, CancellationToken token) {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            using (var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token)) {
                res.EnsureSuccessStatusCode();

                byte[] bytes = await res.Content.ReadAsByteArrayAsync();

                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(bytes)) {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                }
                return bmp;
            }
        }

        private void CorrectRadio_Checked(object sender, RoutedEventArgs e) {
            if (CorrectRadio1.IsChecked != true &&
                CorrectRadio2.IsChecked != true &&
                CorrectRadio3.IsChecked != true) {
                CorrectRadio1.IsChecked = true;
            }
        }

        // -------------------------
        // ★保存：Quiz / Choice を確実にDBへ
        // -------------------------
        private async void Save_Click(object sender, RoutedEventArgs e) {
            await AppDb.InitAsync();

            if (_selectedWork == null) { MessageBox.Show("作品を選択してください。"); return; }

            string createdBy = (CreatedByTextBox.Text ?? "").Trim();
            if (createdBy.Length == 0) createdBy = "anonymous";

            string question = (QuestionTextBox.Text ?? "").Trim();
            if (question.Length == 0 || question == QuestionPlaceholder) {
                MessageBox.Show("問題文を入力してください。");
                return;
            }

            string c1 = (ChoiceTextBox1.Text ?? "").Trim();
            string c2 = (ChoiceTextBox2.Text ?? "").Trim();
            string c3 = (ChoiceTextBox3.Text ?? "").Trim();

            if (c1.Length == 0 || c2.Length == 0 || c3.Length == 0) {
                MessageBox.Show("選択肢は3つすべて入力してください。");
                return;
            }

            int correctIndex =
                CorrectRadio1.IsChecked == true ? 1 :
                CorrectRadio2.IsChecked == true ? 2 :
                CorrectRadio3.IsChecked == true ? 3 : 0;

            if (correctIndex == 0) { MessageBox.Show("正解を1つ選択してください。"); return; }

            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            try {
                // 念のため Work をDBへ（既にあってもOK）
                await AppDb.Connection.InsertOrReplaceAsync(_selectedWork);

                var quiz = new Quiz {
                    WorkKey = _selectedWork.WorkKey, // ★QuizPlayWindowはこれで絞る
                    Type = 1,
                    Question = question,
                    CreatedBy = createdBy,
                    CreatedAt = now
                };

                await AppDb.Connection.InsertAsync(quiz);

                // ★環境差で QuizId が取れない保険
                if (quiz.QuizId <= 0) {
                    quiz.QuizId = await AppDb.Connection.ExecuteScalarAsync<int>("select last_insert_rowid()");
                }

                await AppDb.Connection.InsertAsync(new Choice { QuizId = quiz.QuizId, Text = c1, IsCorrect = (correctIndex == 1) });
                await AppDb.Connection.InsertAsync(new Choice { QuizId = quiz.QuizId, Text = c2, IsCorrect = (correctIndex == 2) });
                await AppDb.Connection.InsertAsync(new Choice { QuizId = quiz.QuizId, Text = c3, IsCorrect = (correctIndex == 3) });

                MessageBox.Show("保存しました。");
                await Movie_AnimeQuizApp.Share.QuizShare.AppendAsync(
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
                MessageBox.Show("保存に失敗しました。\n" + ex.Message);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) {
            Close();
        }
    }

    public class TmdbSuggestItem {
        public string MediaType { get; set; }
        public int TmdbId { get; set; }
        public string Title { get; set; }
        public string PosterThumbUrl { get; set; }
        public string Sub { get; set; }
    }
}

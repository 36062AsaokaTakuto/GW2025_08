// QuizPlayWindow.xaml.cs
using Movie_AnimeQuizApp.Data;
using Movie_AnimeQuizApp.Data.Entities;
using Movie_AnimeQuizApp.QuizRuntime;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Movie_AnimeQuizApp.Views {
    public partial class QuizPlayWindow : Window {
        private readonly string _workKey;
        private readonly int _startQuizId;

        private Work _work;
        private Data.Entities.Quiz _currentQuiz;
        private List<Data.Entities.Quiz> _allQuizzes = new List<Data.Entities.Quiz>();
        private List<Choice> _currentChoices = new List<Choice>();

        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private int _remainingSeconds;

        private const int LimitSeconds = 30;

        private bool _isClosingOrNavigating;

        // ---- 画像高速化 ----
        private static readonly HttpClient _http = new HttpClient();
        private CancellationTokenSource _imageCts;

        private const string TmdbBackdropBase = "https://image.tmdb.org/t/p/w780";
        private const string TmdbPosterBase = "https://image.tmdb.org/t/p/w500";

        private static readonly ConcurrentDictionary<string, BitmapImage> _imgCache
            = new ConcurrentDictionary<string, BitmapImage>();

        private const string InstructionLine = "〇になっている単語を選択肢から選べ。";

        // =====================================================
        // ★追加：QuizPlayで「最初に1回だけ」シャッフルした順番を保持
        // （同じ作品の問題が複数あるとき、出題順をランダム固定にする）
        // =====================================================
        private static readonly object _orderLock = new object();
        private static string _orderWorkKey = "";
        private static List<int> _orderQuizIds = new List<int>();
        private static int _orderPos = 0;
        private static readonly Random _rng = new Random();

        private static void ShuffleInPlace(List<int> list) {
            if (list == null || list.Count <= 1) return;
            for (int i = list.Count - 1; i > 0; i--) {
                int j = _rng.Next(i + 1);
                int tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }

        // forceReset=true の時だけ「最初に1回シャッフル」をやり直す（新セッション開始時）
        private void InitOrKeepShuffledOrder(List<Data.Entities.Quiz> quizzes, bool forceReset, int removeQuizId) {
            lock (_orderLock) {
                bool needReset =
                    forceReset ||
                    !string.Equals(_orderWorkKey, _workKey ?? "", StringComparison.Ordinal) ||
                    _orderQuizIds == null || _orderQuizIds.Count == 0;

                if (needReset) {
                    _orderWorkKey = _workKey ?? "";
                    _orderQuizIds = (quizzes ?? new List<Data.Entities.Quiz>())
                        .Where(q => q != null && q.QuizId > 0)
                        .Select(q => q.QuizId)
                        .Distinct()
                        .ToList();

                    ShuffleInPlace(_orderQuizIds);
                    _orderPos = 0;
                }

                // startQuizId 指定で開始する場合、そのIDが後で重複出題されないように除外
                if (removeQuizId > 0 && _orderQuizIds != null && _orderQuizIds.Count > 0) {
                    _orderQuizIds.Remove(removeQuizId);
                }
            }
        }

        private int PickNextQuizIdFromShuffledOrder() {
            lock (_orderLock) {
                if (_orderQuizIds == null || _orderQuizIds.Count == 0) return 0;
                if (_orderPos >= _orderQuizIds.Count) return 0;

                int id = _orderQuizIds[_orderPos];
                _orderPos++;
                return id;
            }
        }

        public QuizPlayWindow(QuizSession session) {
            InitializeComponent();

            _workKey = (session != null) ? session.WorkKey : "";
            _startQuizId = 0;

            Loaded += QuizPlayWindow_Loaded;

            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;

            Closing += QuizPlayWindow_Closing;
            Closed += QuizPlayWindow_Closed;
        }

        public QuizPlayWindow(string workKey, int quizId) {
            InitializeComponent();

            _workKey = workKey ?? "";
            _startQuizId = quizId;

            Loaded += QuizPlayWindow_Loaded;

            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;

            Closing += QuizPlayWindow_Closing;
            Closed += QuizPlayWindow_Closed;
        }

        private void QuizPlayWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e) {
            _isClosingOrNavigating = true;
            StopTimer();
            try { _imageCts?.Cancel(); } catch { }
        }

        private void QuizPlayWindow_Closed(object sender, EventArgs e) {
            _isClosingOrNavigating = true;
            StopTimer();
            try { _imageCts?.Cancel(); } catch { }
        }

        private async void QuizPlayWindow_Loaded(object sender, RoutedEventArgs e) {
            await AppDb.InitAsync();

            // TLS / UA
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            try {
                if (_http.DefaultRequestHeaders.UserAgent.Count == 0) {
                    _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
                }
            }
            catch { }

            _work = await AppDb.Connection.Table<Work>()
                .Where(w => w.WorkKey == _workKey)
                .FirstOrDefaultAsync();

            if (_work == null) {
                Close();
                return;
            }

            try { WorkTitleText.Text = _work.Title ?? ""; } catch { }

            // 背景画像：非同期
            try { BackgroundImage.Source = null; } catch { }
            try { _imageCts?.Cancel(); } catch { }
            _imageCts = new CancellationTokenSource();
            _ = LoadBackgroundAsync(_work, _imageCts.Token);

            _allQuizzes = await AppDb.Connection.Table<Data.Entities.Quiz>()
                .Where(q => q.WorkKey == _workKey)
                .ToListAsync();

            if (_allQuizzes == null) _allQuizzes = new List<Data.Entities.Quiz>();
            if (_allQuizzes.Count <= 0) {
                Close();
                return;
            }

            // ここは “次の問題” で継続するために残す（外側の画面から開始する時に StartNew する）
            bool startedNewSession = false;
            if (QuizSession.Current == null || !QuizSession.Current.IsSameWork(_workKey)) {
                QuizSession.StartNew(_workKey, _allQuizzes.Count);
                startedNewSession = true;
            }

            // ★ここが本題：最初に1回だけシャッフル（新セッション開始時のみ）
            InitOrKeepShuffledOrder(_allQuizzes, startedNewSession, _startQuizId);

            int quizIdToPlay = _startQuizId;

            // startQuizId が無ければ、シャッフル済み順で次を取る
            if (quizIdToPlay <= 0) {
                quizIdToPlay = PickNextQuizIdFromShuffledOrder();
            }

            if (quizIdToPlay <= 0) {
                Close();
                return;
            }

            await LoadQuizAsync(quizIdToPlay);

            StartTimer(LimitSeconds);
        }

        private void StartTimer(int seconds) {
            StopTimer();
            _remainingSeconds = seconds;
            try { TimerText.Text = _remainingSeconds.ToString(); } catch { }
            _timer.Start();
        }

        private void StopTimer() {
            try { _timer.Stop(); } catch { }
        }

        private void Timer_Tick(object sender, EventArgs e) {
            if (_isClosingOrNavigating) return;

            _remainingSeconds--;
            try { TimerText.Text = _remainingSeconds.ToString(); } catch { }

            if (_remainingSeconds <= 0) {
                _isClosingOrNavigating = true;
                StopTimer();

                if (_currentQuiz != null && QuizSession.Current != null) {
                    QuizSession.Current.RecordResult(_currentQuiz.QuizId, false);
                }

                OpenResultWindow(false, _currentQuiz != null ? _currentQuiz.QuizId : 0);
            }
        }

        private async System.Threading.Tasks.Task LoadQuizAsync(int quizId) {
            _currentQuiz = _allQuizzes.FirstOrDefault(q => q.QuizId == quizId);

            if (_currentQuiz == null) {
                _currentQuiz = _allQuizzes.FirstOrDefault();
                if (_currentQuiz == null) {
                    Close();
                    return;
                }
            }

            try {
                string q = _currentQuiz.Question ?? "";
                q = EnsureNewlineAfterInstruction(q);
                QuestionText.Text = q;
            }
            catch { }

            _currentChoices = await AppDb.Connection.Table<Choice>()
                .Where(c => c.QuizId == _currentQuiz.QuizId)
                .ToListAsync();

            if (_currentChoices == null) _currentChoices = new List<Choice>();

            SetChoiceButton(ChoiceBtn1, _currentChoices, 0);
            SetChoiceButton(ChoiceBtn2, _currentChoices, 1);
            SetChoiceButton(ChoiceBtn3, _currentChoices, 2);
        }

        private static string EnsureNewlineAfterInstruction(string text) {
            if (string.IsNullOrEmpty(text)) return text;

            int idx = text.IndexOf(InstructionLine, StringComparison.Ordinal);
            if (idx < 0) return text;

            int pos = idx + InstructionLine.Length;

            // 末尾なら空行を作る（改行2回）
            if (pos >= text.Length) {
                string nlEnd = text.Contains("\r\n") ? "\r\n" : "\n";
                return text + nlEnd + nlEnd;
            }

            // 指示文の直後のスペース/タブはスキップ
            int p = pos;
            while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) p++;

            // 使う改行コード（基本は既存に合わせる）
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";

            // 直後の改行数（行区切り数）を数える：\r\n は1回としてカウント
            int breaks = 0;
            int scan = p;
            int afterFirstBreak = p;

            while (scan < text.Length && breaks < 2) {
                if (text[scan] == '\r') {
                    if (scan + 1 < text.Length && text[scan + 1] == '\n') scan += 2;
                    else scan += 1;
                    breaks++;
                    if (breaks == 1) afterFirstBreak = scan;
                    continue;
                }
                if (text[scan] == '\n') {
                    scan += 1;
                    breaks++;
                    if (breaks == 1) afterFirstBreak = scan;
                    continue;
                }
                break;
            }

            // すでに空行あり（改行が2回以上）なら何もしない
            if (breaks >= 2) return text;

            // 改行なし → 改行2回入れて空行を作る
            if (breaks == 0) {
                return text.Insert(p, nl + nl);
            }

            // 改行1回だけ → もう1回足して空行を作る
            return text.Insert(afterFirstBreak, nl);
        }

        private void SetChoiceButton(Button btn, List<Choice> choices, int index) {
            if (btn == null) return;

            if (choices != null && index >= 0 && index < choices.Count && choices[index] != null) {
                btn.Content = choices[index].Text ?? "";
                btn.IsEnabled = true;
            } else {
                btn.Content = "";
                btn.IsEnabled = false;
            }
        }

        private async void Choice_Click(object sender, RoutedEventArgs e) {
            if (_isClosingOrNavigating) return;

            _isClosingOrNavigating = true;
            StopTimer();

            if (_currentQuiz == null || _currentChoices == null) return;

            Button btn = sender as Button;
            if (btn == null) return;

            string selectedText = btn.Content as string;
            if (selectedText == null) selectedText = "";

            Choice selected = _currentChoices.FirstOrDefault(c => (c.Text ?? "") == selectedText);
            bool isCorrect = (selected != null && selected.IsCorrect);

            await SavePlayAsync(_currentQuiz.QuizId, isCorrect);

            if (QuizSession.Current != null) {
                QuizSession.Current.RecordResult(_currentQuiz.QuizId, isCorrect);
            }

            OpenResultWindow(isCorrect, _currentQuiz.QuizId);
        }

        private async System.Threading.Tasks.Task SavePlayAsync(int quizId, bool isCorrect) {
            try {
                var play = new Play {
                    QuizId = quizId,
                    User = "default",
                    IsCorrect = isCorrect,
                    PlayedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };
                await AppDb.Connection.InsertAsync(play);
            }
            catch { }
        }

        private void OpenResultWindow(bool isCorrect, int quizId) {
            try {
                var win = new QuizResultWindow(_workKey, quizId, isCorrect);

                // Ownerはホームを引き継ぐ
                win.Owner = this.Owner;
                win.WindowStartupLocation = WindowStartupLocation.CenterOwner;

                win.Show();
            }
            catch { }

            Close();
        }

        // 戻る：検索文字をクリアして、呼び出し元へ戻る
        private void BackButton_Click(object sender, RoutedEventArgs e) {
            if (_isClosingOrNavigating) return;

            _isClosingOrNavigating = true;
            StopTimer();
            try { _imageCts?.Cancel(); } catch { }

            ClearQuizSearchText();

            try {
                // 基本は Owner（呼び出し元）
                var back = this.Owner;

                // Owner が無い/死んでる場合の保険：MainWindow を探す
                if (back == null && Application.Current != null) {
                    foreach (Window w in Application.Current.Windows) {
                        if (w == null) continue;
                        if (w == this) continue;
                        if (w.GetType().Name == "MainWindow") { back = w; break; }
                    }
                }

                if (back != null) {
                    try { back.Show(); } catch { }
                    try { back.Activate(); } catch { }
                }
            }
            catch { }

            Close();
        }

        private void ClearQuizSearchText() {
            try {
                // まず戻り先（Owner優先）
                Window target = this.Owner;

                if (target == null && Application.Current != null) {
                    foreach (Window w in Application.Current.Windows) {
                        if (w == null) continue;
                        if (w == this) continue;
                        if (w.GetType().Name == "MainWindow") { target = w; break; }
                    }
                }

                if (target == null) return;

                // 1) ClearQuizSearchText() を持っていれば呼ぶ
                var mi = target.GetType().GetMethod(
                    "ClearQuizSearchText",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (mi != null && mi.GetParameters().Length == 0) {
                    mi.Invoke(target, null);
                    return;
                }

                // 2) QuizSearchTextBox というフィールドがあれば Text を空にする
                var fi = target.GetType().GetField(
                    "QuizSearchTextBox",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (fi != null) {
                    var tb = fi.GetValue(target) as TextBox;
                    if (tb != null) {
                        tb.Text = "";
                    }
                }
            }
            catch { }
        }

        // ★追加：閉じる（必ず QuizPlayWindow を呼んだ画面へ戻す）
        private void CloseBtn_Click(object sender, RoutedEventArgs e) {
            if (_isClosingOrNavigating) return;

            _isClosingOrNavigating = true;
            StopTimer();
            try { _imageCts?.Cancel(); } catch { }

            try {
                // 基本は Owner（呼び出し元）
                var back = this.Owner;

                // Owner が無い/死んでる場合の保険：MainWindow を探す
                if (back == null && Application.Current != null) {
                    foreach (Window w in Application.Current.Windows) {
                        if (w == null) continue;
                        if (w == this) continue;
                        if (w.GetType().Name == "MainWindow") { back = w; break; }
                    }
                }

                if (back != null) {
                    try { back.Show(); } catch { }
                    try { back.Activate(); } catch { }
                }
            }
            catch { }

            Close();
        }

        // ---- 背景画像 ----
        private async System.Threading.Tasks.Task LoadBackgroundAsync(Work w, CancellationToken token) {
            if (w == null) return;

            try {
                string url = BuildTmdbUrl(TmdbBackdropBase, w.BackdropPath);
                if (string.IsNullOrWhiteSpace(url)) url = BuildTmdbUrl(TmdbPosterBase, w.PosterPath);
                if (string.IsNullOrWhiteSpace(url)) return;

                if (_imgCache.TryGetValue(url, out var cached) && cached != null) {
                    if (!token.IsCancellationRequested) {
                        try { BackgroundImage.Source = cached; } catch { }
                    }
                    return;
                }

                byte[] bytes;
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                using (var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token)) {
                    res.EnsureSuccessStatusCode();
                    bytes = await res.Content.ReadAsByteArrayAsync();
                }

                token.ThrowIfCancellationRequested();

                BitmapImage bmp;
                using (var ms = new MemoryStream(bytes)) {
                    bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 1280;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                }

                _imgCache[url] = bmp;

                if (!token.IsCancellationRequested) {
                    try { BackgroundImage.Source = bmp; } catch { }
                }
            }
            catch {
            }
        }

        private string BuildTmdbUrl(string baseUrl, string pathOrUrl) {
            if (string.IsNullOrWhiteSpace(pathOrUrl)) return "";
            if (pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return pathOrUrl;

            string p = pathOrUrl.StartsWith("/") ? pathOrUrl : ("/" + pathOrUrl);
            return baseUrl + p;
        }
    }
}
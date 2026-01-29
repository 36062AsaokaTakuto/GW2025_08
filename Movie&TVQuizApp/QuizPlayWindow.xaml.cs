using Movie_AnimeQuizApp.Data;
using Movie_AnimeQuizApp.Data.Entities;
using Movie_AnimeQuizApp.QuizRuntime;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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

        // ★制限時間：30秒
        private const int LimitSeconds = 30;

        private const string TmdbPosterBase = "https://image.tmdb.org/t/p/w342";
        private const string TmdbBackdropBase = "https://image.tmdb.org/t/p/w780";

        private static readonly HttpClient _http = new HttpClient();

        // ★「再度開かれた時に最初から」を安全にするため
        private bool _isReady; // 問題が読み込めた後のみタイマー再開する

        public QuizPlayWindow(QuizSession session) {
            InitializeComponent();

            _workKey = (session != null) ? session.WorkKey : "";
            _startQuizId = 0;

            Loaded += QuizPlayWindow_Loaded;

            // ★閉じたらタイマー停止
            Closing += QuizPlayWindow_Closing;
            Closed += QuizPlayWindow_Closed;

            // ★Hide→Show等でも最初から再スタート
            IsVisibleChanged += QuizPlayWindow_IsVisibleChanged;

            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;
        }

        public QuizPlayWindow(string workKey, int quizId) {
            InitializeComponent();

            _workKey = workKey ?? "";
            _startQuizId = quizId;

            Loaded += QuizPlayWindow_Loaded;

            // ★閉じたらタイマー停止
            Closing += QuizPlayWindow_Closing;
            Closed += QuizPlayWindow_Closed;

            // ★Hide→Show等でも最初から再スタート
            IsVisibleChanged += QuizPlayWindow_IsVisibleChanged;

            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;
        }

        private async void QuizPlayWindow_Loaded(object sender, RoutedEventArgs e) {
            await AppDb.InitAsync();
            MessageBox.Show(Movie_AnimeQuizApp.Share.QuizShare.GetShareFilePath());
            try { await Movie_AnimeQuizApp.Share.QuizShare.ImportToDbAsync(); } catch { }


            _work = await AppDb.Connection.Table<Work>()
                .Where(w => w.WorkKey == _workKey)
                .FirstOrDefaultAsync();

            if (_work == null) {
                Close();
                return;
            }

            try { WorkTitleText.Text = _work.Title ?? ""; } catch { }

            // ★背景：TMDB（Backdrop優先→Poster）
            string bgUrl = ToBackdropUrl(_work.BackdropPath);
            if (string.IsNullOrWhiteSpace(bgUrl)) bgUrl = ToPosterUrl(_work.PosterPath);
            await SetBackgroundImageAsync(bgUrl);

            _allQuizzes = await AppDb.Connection.Table<Data.Entities.Quiz>()
                .Where(q => q.WorkKey == _workKey)
                .ToListAsync();

            if (_allQuizzes == null) _allQuizzes = new List<Data.Entities.Quiz>();
            if (_allQuizzes.Count <= 0) {
                Close();
                return;
            }

            if (QuizSession.Current == null || !QuizSession.Current.IsSameWork(_workKey)) {
                QuizSession.StartNew(_workKey, _allQuizzes.Count);
            }

            int quizIdToPlay = _startQuizId;
            if (quizIdToPlay <= 0) {
                quizIdToPlay = QuizSession.Current.PickNextQuizIdAndMark(_allQuizzes);
            }

            if (quizIdToPlay <= 0) {
                Close();
                return;
            }

            await LoadQuizAsync(quizIdToPlay);

            // ★ここまで来たらタイマー再開OK
            _isReady = true;

            // ★最初からカウント開始
            RestartTimerFromBeginning();
        }

        private void QuizPlayWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) {
            // ★再表示されたら最初から数えなおす
            if (this.IsVisible) {
                if (_isReady) {
                    RestartTimerFromBeginning();
                }
            } else {
                // ★非表示/閉じる方向なら停止
                StopTimer();
            }
        }

        private void QuizPlayWindow_Closing(object sender, CancelEventArgs e) {
            // ★閉じたらタイマー停止
            StopTimer();
        }

        private void QuizPlayWindow_Closed(object sender, EventArgs e) {
            // ★完全に閉じた後も念のため
            StopTimer();
            _timer.Tick -= Timer_Tick;
            _isReady = false;
        }

        private void RestartTimerFromBeginning() {
            StopTimer();
            _remainingSeconds = LimitSeconds;
            try { TimerText.Text = _remainingSeconds.ToString(); } catch { }
            _timer.Start();
        }

        private void StopTimer() {
            try { _timer.Stop(); } catch { }
        }

        private void Timer_Tick(object sender, EventArgs e) {
            _remainingSeconds--;
            try { TimerText.Text = _remainingSeconds.ToString(); } catch { }

            if (_remainingSeconds <= 0) {
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

            try { QuestionText.Text = _currentQuiz.Question ?? ""; } catch { }

            _currentChoices = await AppDb.Connection.Table<Choice>()
                .Where(c => c.QuizId == _currentQuiz.QuizId)
                .ToListAsync();

            if (_currentChoices == null) _currentChoices = new List<Choice>();

            SetChoiceButton(ChoiceBtn1, _currentChoices, 0);
            SetChoiceButton(ChoiceBtn2, _currentChoices, 1);
            SetChoiceButton(ChoiceBtn3, _currentChoices, 2);
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
            catch {
            }
        }

        private void OpenResultWindow(bool isCorrect, int quizId) {
            try {
                var win = new QuizResultWindow(_workKey, quizId, isCorrect);

                // ★全画面（最大化）
                win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                win.WindowState = WindowState.Maximized;

                win.Show();
            }
            catch {
            }

            Close();
        }


        private string ToPosterUrl(string posterPathOrUrl) {
            if (string.IsNullOrWhiteSpace(posterPathOrUrl)) return "";
            if (posterPathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return posterPathOrUrl;

            // "/xxx.jpg" でも "xxx.jpg" でもOK
            if (!posterPathOrUrl.StartsWith("/")) posterPathOrUrl = "/" + posterPathOrUrl;
            return TmdbPosterBase + posterPathOrUrl;
        }

        private string ToBackdropUrl(string backdropPathOrUrl) {
            if (string.IsNullOrWhiteSpace(backdropPathOrUrl)) return "";
            if (backdropPathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return backdropPathOrUrl;

            // "/xxx.jpg" でも "xxx.jpg" でもOK
            if (!backdropPathOrUrl.StartsWith("/")) backdropPathOrUrl = "/" + backdropPathOrUrl;
            return TmdbBackdropBase + backdropPathOrUrl;
        }

        // ★背景が出ない対策：HttpClientで取得→StreamSourceで表示（安定）
        private async System.Threading.Tasks.Task SetBackgroundImageAsync(string imageUrl) {
            if (string.IsNullOrWhiteSpace(imageUrl)) {
                try { BackgroundImage.Source = null; } catch { }
                return;
            }

            try {
                // TLS 1.2 対応（古い環境対策）
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

                // 保険：User-Agent
                if (_http.DefaultRequestHeaders.UserAgent.Count == 0) {
                    _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
                }

                byte[] bytes = await _http.GetByteArrayAsync(imageUrl);

                using (var ms = new MemoryStream(bytes)) {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    BackgroundImage.Source = bmp;
                }
            }
            catch {
                try { BackgroundImage.Source = null; } catch { }
            }
        }
    }
}

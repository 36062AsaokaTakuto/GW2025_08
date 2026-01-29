using Movie_AnimeQuizApp.Data;
using Movie_AnimeQuizApp.Data.Entities;
using Movie_AnimeQuizApp.QuizRuntime;
using Movie_AnimeQuizApp.Share;
using System;
using System.Collections.Generic;
using System.Linq;
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

        private const int LimitSeconds = 60;
        private const string TmdbPosterBase = "https://image.tmdb.org/t/p/w342";

        public QuizPlayWindow(QuizSession session) {
            InitializeComponent();

            _workKey = (session != null) ? session.WorkKey : "";
            _startQuizId = 0;

            Loaded += QuizPlayWindow_Loaded;

            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;
        }

        public QuizPlayWindow(string workKey, int quizId) {
            InitializeComponent();

            _workKey = workKey ?? "";
            _startQuizId = quizId;

            Loaded += QuizPlayWindow_Loaded;

            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;
        }

        private async void QuizPlayWindow_Loaded(object sender, RoutedEventArgs e) {
            await AppDb.InitAsync();

            // ★最重要：DB検索より前に共有クイズを取り込む
            try { await QuizShare.ImportToDbAsync(); } catch { }

            // Work 取得
            _work = await AppDb.Connection.Table<Work>()
                .Where(w => w.WorkKey == _workKey)
                .FirstOrDefaultAsync();

            if (_work == null) {
                Close();
                return;
            }

            // タイトル/ポスター
            try { WorkTitleText.Text = _work.Title ?? ""; } catch { }
            SetPosterImage(ToPosterUrl(_work.PosterPath));

            // 同作品のクイズ全件
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

            StartTimer(LimitSeconds);
        }

        private string ToPosterUrl(string posterPathOrUrl) {
            if (string.IsNullOrWhiteSpace(posterPathOrUrl)) return "";
            if (posterPathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return posterPathOrUrl;
            if (posterPathOrUrl.StartsWith("/")) return TmdbPosterBase + posterPathOrUrl;
            return posterPathOrUrl;
        }

        private void StartTimer(int seconds) {
            _remainingSeconds = seconds;
            try { TimerText.Text = _remainingSeconds.ToString(); } catch { }
            _timer.Start();
        }

        private void StopTimer() {
            _timer.Stop();
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
                Play play = new Play {
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
                QuizResultWindow win = new QuizResultWindow(_workKey, quizId, isCorrect);
                win.Owner = this.Owner;
                win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                win.Show();
            }
            catch { }

            Close();
        }

        private void SetPosterImage(string posterPathOrUrl) {
            if (string.IsNullOrWhiteSpace(posterPathOrUrl)) return;

            try {
                BitmapImage bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(posterPathOrUrl, UriKind.RelativeOrAbsolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
            }
            catch { }
        }
    }
}

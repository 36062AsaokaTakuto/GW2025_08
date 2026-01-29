using Movie_AnimeQuizApp.Data;
using Movie_AnimeQuizApp.Data.Entities;
using Movie_AnimeQuizApp.QuizRuntime;
using Movie_AnimeQuizApp.Share;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Movie_AnimeQuizApp.Views {
    public partial class QuizResultWindow : Window {
        private readonly string _workKey;
        private readonly int _quizId;
        private readonly bool _isCorrect;

        private List<Quiz> _allQuizzes = new List<Quiz>();

        private const string TmdbBackdropBase = "https://image.tmdb.org/t/p/w1280";
        private const string TmdbPosterBase = "https://image.tmdb.org/t/p/w780";

        private static readonly HttpClient _http = new HttpClient();

        public QuizResultWindow(string workKey, int quizId, bool isCorrect) {
            InitializeComponent();
            _workKey = workKey ?? "";
            _quizId = quizId;
            _isCorrect = isCorrect;

            Loaded += QuizResultWindow_Loaded;
        }

        private async void QuizResultWindow_Loaded(object sender, RoutedEventArgs e) {
            await AppDb.InitAsync();

            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            try {
                if (_http.DefaultRequestHeaders.UserAgent.Count == 0) {
                    _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
                }
            }
            catch { }

            // ★ここは変更しない（指定ブロックそのまま）
            ResultText.Text = _isCorrect ? "〇" : "✖";
            ResultText.Foreground = _isCorrect ? Brushes.LimeGreen : Brushes.Red;

            // ★×だけ大きくする（RenderTransform）
            double scale = _isCorrect ? 1.0 : 1.05;   // 1.20～1.60で調整
            ResultText.RenderTransformOrigin = new Point(0.5, 0.5);
            ResultText.RenderTransform = new ScaleTransform(scale, scale);

            // 背景は並行で（文字を先に出す）
            var bgTask = LoadBackgroundAsync(_workKey);

            // 文字（問題文・正解）は先に
            await LoadQuizAndCorrectAnswerAsync(_quizId);

            // ★分母：この作品の登録問題数（最新）
            int total = 0;
            try {
                total = await AppDb.Connection.Table<Quiz>()
                    .Where(q => q.WorkKey == _workKey)
                    .CountAsync();
            }
            catch {
                total = 0;
            }

            // ★次の問題へ用：同じく最新リストを取り直す（新規登録分も含む）
            try {
                _allQuizzes = await AppDb.Connection.Table<Quiz>()
                    .Where(q => q.WorkKey == _workKey)
                    .ToListAsync();
            }
            catch {
                _allQuizzes = new List<Quiz>();
            }

            // 正解数はセッションのCorrectCount（同一作品のときだけ）
            int correct = 0;
            if (QuizSession.Current != null && QuizSession.Current.IsSameWork(_workKey)) {
                correct = QuizSession.Current.CorrectCount;
            }
            ProgressText.Text = correct.ToString() + " / " + total.ToString();

            // ★残りが無ければ Next を押せない
            bool canNext = false;
            if (QuizSession.Current != null && QuizSession.Current.IsSameWork(_workKey)) {
                canNext = QuizSession.Current.HasRemaining(_allQuizzes);
            }
            NextBtn.IsEnabled = canNext;

            // 背景の完了は最後でOK
            try { await bgTask; } catch { }
        }

        private async System.Threading.Tasks.Task LoadBackgroundAsync(string workKey) {
            try {
                Work w = await AppDb.Connection.Table<Work>()
                    .Where(x => x.WorkKey == workKey)
                    .FirstOrDefaultAsync();

                if (w == null) { BackgroundImage.Source = null; return; }

                string bgUrl = ToBackdropUrl(w.BackdropPath);
                if (string.IsNullOrWhiteSpace(bgUrl)) bgUrl = ToPosterUrl(w.PosterPath);
                if (string.IsNullOrWhiteSpace(bgUrl)) { BackgroundImage.Source = null; return; }

                byte[] bytes = await _http.GetByteArrayAsync(bgUrl);

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

        private string ToBackdropUrl(string pathOrUrl) {
            if (string.IsNullOrWhiteSpace(pathOrUrl)) return "";
            if (pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return pathOrUrl;
            if (!pathOrUrl.StartsWith("/")) pathOrUrl = "/" + pathOrUrl;
            return TmdbBackdropBase + pathOrUrl;
        }

        private string ToPosterUrl(string pathOrUrl) {
            if (string.IsNullOrWhiteSpace(pathOrUrl)) return "";
            if (pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return pathOrUrl;
            if (!pathOrUrl.StartsWith("/")) pathOrUrl = "/" + pathOrUrl;
            return TmdbPosterBase + pathOrUrl;
        }

        private async System.Threading.Tasks.Task LoadQuizAndCorrectAnswerAsync(int quizId) {
            try {
                var quiz = await AppDb.Connection.Table<Quiz>()
                    .Where(q => q.QuizId == quizId)
                    .FirstOrDefaultAsync();

                QuestionText.Text = (quiz != null) ? (quiz.Question ?? "") : "";

                var correctChoice = await AppDb.Connection.Table<Choice>()
                    .Where(c => c.QuizId == quizId && c.IsCorrect == true)
                    .FirstOrDefaultAsync();

                string ans = (correctChoice != null) ? (correctChoice.Text ?? "") : "";
                if (string.IsNullOrWhiteSpace(ans)) ans = "（不明）";

                CorrectAnswerText.Text = "正解は" + ans + "です。";
            }
            catch {
                try { CorrectAnswerText.Text = "正解は（不明）です。"; } catch { }
                try { QuestionText.Text = ""; } catch { }
            }
        }

        private async void NextBtn_Click(object sender, RoutedEventArgs e) {
            if (QuizSession.Current == null) { NextBtn.IsEnabled = false; return; }
            if (!QuizSession.Current.IsSameWork(_workKey)) { NextBtn.IsEnabled = false; return; }

            // ★追加：次の問題を探す前に共有ファイル→DB取り込み
            try { await QuizShare.ImportToDbAsync(); } catch { }

            // ★念のため毎回最新に（新規登録が途中で増えても追従）
            try {
                _allQuizzes = await AppDb.Connection.Table<Quiz>()
                    .Where(q => q.WorkKey == _workKey)
                    .ToListAsync();
            }
            catch {
                _allQuizzes = new List<Quiz>();
            }

            if (!QuizSession.Current.HasRemaining(_allQuizzes)) {
                NextBtn.IsEnabled = false;
                return;
            }

            int nextQuizId = QuizSession.Current.PickNextQuizIdAndMark(_allQuizzes);
            if (nextQuizId <= 0) {
                NextBtn.IsEnabled = false;
                return;
            }

            var win = new QuizPlayWindow(_workKey, nextQuizId);
            win.Owner = this.Owner;

            win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            win.WindowState = WindowState.Maximized;

            win.Show();
            Close();
        }


        private void CloseBtn_Click(object sender, RoutedEventArgs e) {
            // ★閉じるボタンで正答率リセット（セッション破棄）
            QuizSession.Clear();
            Close();
        }
    }
}

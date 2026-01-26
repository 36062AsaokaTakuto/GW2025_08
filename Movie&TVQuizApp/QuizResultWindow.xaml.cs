using Movie_AnimeQuizApp.Data;
using Movie_AnimeQuizApp.Data.Entities;
using Movie_AnimeQuizApp.QuizRuntime;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Movie_AnimeQuizApp.Views {
    public partial class QuizResultWindow : Window {
        private readonly string _workKey;
        private readonly int _quizId;
        private readonly bool _isCorrect;

        private List<Quiz> _allQuizzes = new List<Quiz>();

        public QuizResultWindow(string workKey, int quizId, bool isCorrect) {
            InitializeComponent();
            _workKey = workKey;
            _quizId = quizId;
            _isCorrect = isCorrect;

            Loaded += QuizResultWindow_Loaded;
        }

        private async void QuizResultWindow_Loaded(object sender, RoutedEventArgs e) {
            await AppDb.InitAsync();

            // ★文字ではなく「〇×」
            ResultText.Text = _isCorrect ? "〇" : "×";

            _allQuizzes = await AppDb.Connection.Table<Quiz>()
                .Where(q => q.WorkKey == _workKey)
                .ToListAsync();

            int total = (_allQuizzes != null) ? _allQuizzes.Count : 0;

            // ★分母は「QuizSearchHitクリック時に確定した総数」で固定
            if (QuizSession.Current != null && QuizSession.Current.IsSameWork(_workKey)) {
                total = QuizSession.Current.TotalQuizCount;
                ProgressText.Text = QuizSession.Current.CorrectCount.ToString() + " / " + total.ToString();
            } else {
                ProgressText.Text = "0 / " + total.ToString();
            }

            // ★全部出し切ったら Next を押せない
            bool canNext = false;
            if (QuizSession.Current != null && QuizSession.Current.IsSameWork(_workKey)) {
                canNext = QuizSession.Current.HasRemaining(_allQuizzes);
            }
            NextBtn.IsEnabled = canNext;
        }

        private void NextBtn_Click(object sender, RoutedEventArgs e) {
            if (QuizSession.Current == null) return;
            if (!QuizSession.Current.IsSameWork(_workKey)) return;
            if (_allQuizzes == null || _allQuizzes.Count == 0) return;

            // ★残りが無いなら何もしない（ボタン自体も無効のはず）
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
            win.Show();
            Close();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) {
            Close();
        }
    }
}

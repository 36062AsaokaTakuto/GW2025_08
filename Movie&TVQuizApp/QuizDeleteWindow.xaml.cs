// QuizDeleteWindow.xaml.cs（修正後・全体）
// 変更点：作成者プレースホルダー制御（Watermark表示/非表示）
// ※削除やDB処理はそのまま
using Movie_AnimeQuizApp.Data;
using Movie_AnimeQuizApp.Data.Entities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Movie_AnimeQuizApp.Views {
    public partial class QuizDeleteWindow : Window {

        private readonly ObservableCollection<QuizDeleteItem> _items = new ObservableCollection<QuizDeleteItem>();

        public QuizDeleteWindow() {
            InitializeComponent();
            QuizList.ItemsSource = _items;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e) {
            UpdateCreatedByWatermark();
            await ReloadAsync();
        }

        // ★Watermark表示制御
        private void UpdateCreatedByWatermark() {
            if (CreatedByWatermark == null || CreatedByTextBox == null) return;

            string t = CreatedByTextBox.Text ?? "";
            CreatedByWatermark.Visibility = (t.Trim().Length == 0)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void CreatedBy_GotFocus(object sender, RoutedEventArgs e) {
            UpdateCreatedByWatermark();
        }

        private void CreatedBy_LostFocus(object sender, RoutedEventArgs e) {
            UpdateCreatedByWatermark();
        }

        private void CreatedByTextBox_TextChanged(object sender, TextChangedEventArgs e) {
            UpdateCreatedByWatermark();
        }

        private async void Reload_Click(object sender, RoutedEventArgs e) {
            await ReloadAsync();
        }

        private async Task ReloadAsync() {
            await AppDb.InitAsync();

            string createdBy = (CreatedByTextBox.Text ?? "").Trim();
            if (createdBy.Length == 0) {
                StatusText.Text = "作成者を入力してください。";
                _items.Clear();
                return;
            }

            var quizzes = await AppDb.Connection.Table<Quiz>()
                .Where(q => q.CreatedBy == createdBy)
                .ToListAsync();

            if (quizzes == null) quizzes = new List<Quiz>();

            // ★追加：作成日順（古い→新しい）に並べ替え
            // CreatedAt が "yyyy-MM-dd HH:mm:ss" 前提なので文字列比較でOK
            quizzes = quizzes
                .OrderBy(q => (q != null ? (q.CreatedAt ?? "") : ""))
                .ThenBy(q => (q != null ? q.QuizId : 0))
                .ToList();

            var works = await AppDb.Connection.Table<Work>().ToListAsync();
            if (works == null) works = new List<Work>();

            var workDict = works
                .Where(w => w != null && !string.IsNullOrWhiteSpace(w.WorkKey))
                .GroupBy(w => w.WorkKey)
                .ToDictionary(g => g.Key, g => g.First());

            _items.Clear();

            for (int i = 0; i < quizzes.Count; i++) {
                var q = quizzes[i];
                if (q == null) continue;

                Work w;
                workDict.TryGetValue(q.WorkKey ?? "", out w);

                string title = (w != null && !string.IsNullOrWhiteSpace(w.Title)) ? w.Title : (q.WorkKey ?? "");
                string question = q.Question ?? "";
                if (question.Length > 140) question = question.Substring(0, 140) + "…";

                _items.Add(new QuizDeleteItem {
                    QuizId = q.QuizId,
                    WorkKey = q.WorkKey ?? "",
                    WorkTitle = title,
                    Question = question,
                    Meta = "作成日: " + (q.CreatedAt ?? "") + " / Type: " + q.Type.ToString()
                });
            }

            StatusText.Text = "件数: " + _items.Count.ToString();
        }


        private async void DeleteSelected_Click(object sender, RoutedEventArgs e) {
            var sel = QuizList.SelectedItem as QuizDeleteItem;
            if (sel == null) {
                MessageBox.Show("削除するクイズを選択してください。");
                return;
            }

            await AppDb.InitAsync();

            try {
                await AppDb.Connection.ExecuteAsync("DELETE FROM [Choice] WHERE QuizId = ?", sel.QuizId);
                await AppDb.Connection.ExecuteAsync("DELETE FROM [Play]   WHERE QuizId = ?", sel.QuizId);
                await AppDb.Connection.ExecuteAsync("DELETE FROM [Quiz]   WHERE QuizId = ?", sel.QuizId);

                _items.Remove(sel);
                StatusText.Text = "件数: " + _items.Count.ToString();
            }
            catch (Exception ex) {
                MessageBox.Show("削除に失敗しました。\n" + ex.Message);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) {
            Close();
        }
    }

    public class QuizDeleteItem {
        public int QuizId { get; set; }
        public string WorkKey { get; set; }
        public string WorkTitle { get; set; }
        public string Question { get; set; }
        public string Meta { get; set; }
    }
}

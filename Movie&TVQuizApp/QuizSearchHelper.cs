using Movie_AnimeQuizApp.Data;
using Movie_AnimeQuizApp.Data.Entities;
using Movie_AnimeQuizApp.QuizRuntime;   // ★QuizSession の場所
using Movie_AnimeQuizApp.Share;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Movie_AnimeQuizApp.Views {
    public static class QuizSearchHelper {
        // 3画面共通：Window内の QuizSearchPanel から文字を拾ってクイズ開始
        // ★検索ボタン名は QuizSearchHit（各WindowのClickでこれを呼ぶ）
        public static async Task StartQuizFromWindowAsync(Window host) {
            if (host == null) return;

            string title = GetQueryText(host);
            if (string.IsNullOrWhiteSpace(title)) return;

            await AppDb.InitAsync();

            // ★追加：DB検索より前に共有ファイル→DB取り込み
            try { await QuizShare.ImportToDbAsync(); } catch { }

            // ★完全一致のみ
            Work work = await AppDb.Connection.Table<Work>()
                .Where(w => w.Title == title)
                .FirstOrDefaultAsync();

            if (work == null) return;

            // ★このクリック時点の総数を確定（分母固定）
            int total = await AppDb.Connection.Table<Quiz>()
                .Where(q => q.WorkKey == work.WorkKey)
                .CountAsync();

            if (total <= 0) return;

            QuizSession.StartNew(work.WorkKey, total);

            var allQuizzes = await AppDb.Connection.Table<Quiz>()
                .Where(q => q.WorkKey == work.WorkKey)
                .ToListAsync();

            int firstQuizId = QuizSession.Current.PickNextQuizIdAndMark(allQuizzes);
            if (firstQuizId <= 0) return;

            var win = new QuizPlayWindow(work.WorkKey, firstQuizId);

            win.Owner = host;
            win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            win.Show();
        }


        private static string GetQueryText(Window host) {
            object obj = host.FindName("QuizSearchPanel");

            // QuizSearchPanel 自体が TextBox の場合
            TextBox tb = obj as TextBox;
            if (tb != null) return (tb.Text ?? "").Trim();

            // QuizSearchPanel が StackPanel 等で、その中に TextBox がある場合
            FrameworkElement fe = obj as FrameworkElement;
            if (fe != null) {
                TextBox inner = FindVisualChild<TextBox>(fe);
                if (inner != null) return (inner.Text ?? "").Trim();
            }

            // 保険：Window全体から探す
            TextBox anywhere = FindVisualChild<TextBox>(host);
            if (anywhere != null && anywhere.Name == "QuizSearchPanel") {
                return (anywhere.Text ?? "").Trim();
            }

            return "";
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject {
            if (parent == null) return null;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++) {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                T typed = child as T;
                if (typed != null) return typed;

                T rec = FindVisualChild<T>(child);
                if (rec != null) return rec;
            }
            return null;
        }
    }
}

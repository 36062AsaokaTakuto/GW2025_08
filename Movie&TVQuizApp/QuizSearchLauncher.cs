using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using Movie_AnimeQuizApp.Data;
using Movie_AnimeQuizApp.Data.Entities;
using Movie_AnimeQuizApp.Views;
using QuizEntity = Movie_AnimeQuizApp.Data.Entities.Quiz;

namespace Movie_AnimeQuizApp.QuizRuntime {
    public static class QuizLauncher {
        /// <summary>
        /// タイトル完全一致で Work を探してクイズ開始（QuizSearchHit用）
        /// </summary>
        public static async Task StartByExactTitleAsync(Window owner, string rawTitle) {
            string title = (rawTitle ?? "").Trim();
            if (string.IsNullOrEmpty(title)) return;

            await AppDb.InitAsync();

            // ★完全一致
            Work work = await AppDb.Connection.Table<Work>()
                .Where(w => w.Title == title)
                .FirstOrDefaultAsync();

            if (work == null) {
                MessageBox.Show("作品が見つかりません（タイトル完全一致）");
                return;
            }

            string workKey = work.WorkKey;

            List<QuizEntity> quizzes = await AppDb.Connection.Table<QuizEntity>()
                .Where(q => q.WorkKey == workKey)
                .ToListAsync();

            if (quizzes == null || quizzes.Count == 0) {
                MessageBox.Show("この作品のクイズが登録されていません");
                return;
            }

            // ★QuizSearchHit クリックごとに新セッション開始 → 分母固定
            QuizSession.StartNew(workKey, quizzes.Count);

            // ★1問目を引いて「出題済み」にする（以後 Next で重複しない）
            int firstQuizId = QuizSession.Current.PickNextQuizIdAndMark(quizzes);
            if (firstQuizId <= 0) return;

            var win = new QuizPlayWindow(workKey, firstQuizId);
            if (owner != null) win.Owner = owner;
            win.Show();
        }
    }
}

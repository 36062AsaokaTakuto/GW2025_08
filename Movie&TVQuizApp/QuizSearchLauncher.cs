using Movie_AnimeQuizApp.Data;
using Movie_AnimeQuizApp.Data.Entities;
using Movie_AnimeQuizApp.Share;
using Movie_AnimeQuizApp.Views;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
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

            // ★追加：DB検索より前に共有ファイル→DB取り込み
            try { await QuizShare.ImportToDbAsync(); } catch { }

            // ★完全一致
            Work work = await AppDb.Connection.Table<Work>()
                .Where(w => w.Title == title)
                .FirstOrDefaultAsync();

            if (work == null) {
                MessageBox.Show("作品が見つかりません（タイトル完全一致）");
                return;
            }

            string workKey = work.WorkKey;

            // ※ここが QuizEntity だと共有取り込み先のテーブルとズレる可能性がある
            // 共有取り込みは Data.Entities.Quiz に入るので、原則 Quiz を使う
            List<Quiz> quizzes = await AppDb.Connection.Table<Quiz>()
                .Where(q => q.WorkKey == workKey)
                .ToListAsync();

            if (quizzes == null || quizzes.Count == 0) {
                MessageBox.Show("この作品のクイズが登録されていません");
                return;
            }

            QuizSession.StartNew(workKey, quizzes.Count);

            int firstQuizId = QuizSession.Current.PickNextQuizIdAndMark(quizzes);
            if (firstQuizId <= 0) return;

            var win = new QuizPlayWindow(workKey, firstQuizId);
            if (owner != null) win.Owner = owner;
            win.Show();
        }

    }
}

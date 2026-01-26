using System;
using System.Collections.Generic;
using System.Linq;
using Movie_AnimeQuizApp.Data.Entities;

namespace Movie_AnimeQuizApp.QuizRuntime {
    public class QuizSession {
        public static QuizSession Current { get; private set; }

        public string WorkKey { get; private set; }
        public int TotalQuizCount { get; private set; }   // ★分母固定（QuizSearchHitクリック時の総数）
        public int CorrectCount { get; private set; }

        // セッション中に出したQuizId（重複防止）
        private readonly HashSet<int> _usedQuizIds = new HashSet<int>();

        private QuizSession(string workKey, int total) {
            WorkKey = workKey ?? "";
            TotalQuizCount = total;
            CorrectCount = 0;
        }

        public static void StartNew(string workKey, int total) {
            Current = new QuizSession(workKey, total);
        }

        public bool IsSameWork(string workKey) {
            return string.Equals(WorkKey, workKey ?? "", StringComparison.Ordinal);
        }

        // ★次のQuizIdを重複なしで選んで「出題済み」にする
        public int PickNextQuizIdAndMark(List<Quiz> allQuizzes) {
            if (allQuizzes == null || allQuizzes.Count == 0) return 0;

            // 同一Workのみに絞る
            var candidates = allQuizzes
                .Where(q => q != null && q.WorkKey == WorkKey && !_usedQuizIds.Contains(q.QuizId))
                .Select(q => q.QuizId)
                .ToList();

            if (candidates.Count == 0) return 0;

            // ランダム
            int idx = new Random().Next(candidates.Count);
            int picked = candidates[idx];

            _usedQuizIds.Add(picked);
            return picked;
        }

        // ★残り問題があるか（結果画面で Next ボタンの有効/無効に使う）
        public bool HasRemaining(List<Quiz> allQuizzes) {
            if (allQuizzes == null || allQuizzes.Count == 0) return false;

            for (int i = 0; i < allQuizzes.Count; i++) {
                var q = allQuizzes[i];
                if (q == null) continue;
                if (q.WorkKey != WorkKey) continue;

                if (!_usedQuizIds.Contains(q.QuizId))
                    return true;
            }
            return false;
        }

        // ★回答結果の記録（進捗表示用）
        public void RecordResult(int quizId, bool isCorrect) {
            // quizIdが未出題でここに来た場合も保険で入れる
            if (quizId > 0) _usedQuizIds.Add(quizId);

            if (isCorrect) CorrectCount++;
        }
    }
}

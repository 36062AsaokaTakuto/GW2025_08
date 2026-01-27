using System;
using System.Collections.Generic;
using System.Linq;
using Movie_AnimeQuizApp.Data.Entities;

namespace Movie_AnimeQuizApp.QuizRuntime {
    public class QuizSession {
        public static QuizSession Current { get; private set; }

        public string WorkKey { get; private set; }
        public int TotalQuizCount { get; private set; }
        public int CorrectCount { get; private set; }

        private readonly HashSet<int> _usedQuizIds = new HashSet<int>();

        private QuizSession(string workKey, int total) {
            WorkKey = workKey ?? "";
            TotalQuizCount = total;
            CorrectCount = 0;
        }

        public static void StartNew(string workKey, int total) {
            Current = new QuizSession(workKey, total);
        }

        // ★追加：セッションを破棄（正答率・出題済みをリセット）
        public static void Clear() {
            Current = null;
        }

        public bool IsSameWork(string workKey) {
            return string.Equals(WorkKey, workKey ?? "", StringComparison.Ordinal);
        }

        public int PickNextQuizIdAndMark(List<Movie_AnimeQuizApp.Data.Entities.Quiz> allQuizzes) {
            if (allQuizzes == null || allQuizzes.Count == 0) return 0;

            var candidates = allQuizzes
                .Where(q => q != null && q.WorkKey == WorkKey && !_usedQuizIds.Contains(q.QuizId))
                .Select(q => q.QuizId)
                .ToList();

            if (candidates.Count == 0) return 0;

            int idx = new System.Random().Next(candidates.Count);
            int picked = candidates[idx];

            _usedQuizIds.Add(picked);
            return picked;
        }

        public bool HasRemaining(List<Movie_AnimeQuizApp.Data.Entities.Quiz> allQuizzes) {
            if (allQuizzes == null || allQuizzes.Count == 0) return false;

            for (int i = 0; i < allQuizzes.Count; i++) {
                var q = allQuizzes[i];
                if (q == null) continue;
                if (q.WorkKey != WorkKey) continue;
                if (!_usedQuizIds.Contains(q.QuizId)) return true;
            }
            return false;
        }

        public void RecordResult(int quizId, bool isCorrect) {
            if (quizId > 0) _usedQuizIds.Add(quizId);
            if (isCorrect) CorrectCount++;
        }
    }
}

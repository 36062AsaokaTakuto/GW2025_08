using Movie_AnimeQuizApp.Data;
using Movie_AnimeQuizApp.Data.Entities;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Movie_AnimeQuizApp.Share {
    // 1行=1イベント（upsert/delete）のJSON（NDJSON）でGit共有
    public static class QuizShare {
        private const string FolderName = "shared";
        private const string FileName = "quizzes.ndjson";

        public class SharedChoiceDto {
            public string Text { get; set; }
            public bool IsCorrect { get; set; }
        }

        public class SharedWorkDto {
            public string WorkKey { get; set; }
            public int TmdbId { get; set; }
            public string MediaType { get; set; }
            public string Title { get; set; }
            public string Overview { get; set; }
            public string PosterPath { get; set; }
            public string BackdropPath { get; set; }
            public string ReleaseDate { get; set; }
        }

        public class SharedQuizDto {
            // ★追加：イベント種別（省略時は upsert 扱いで後方互換）
            public string Action { get; set; }      // "upsert" / "delete"

            // ★追加：安定キー（PCごとにQuizIdが違うのでこれで同一判定）
            public string Key { get; set; }         // sha256

            public SharedWorkDto Work { get; set; }
            public int Type { get; set; }           // 1固定想定
            public string Question { get; set; }
            public string CreatedBy { get; set; }
            public string CreatedAt { get; set; }
            public List<SharedChoiceDto> Choices { get; set; }
        }

        // リポジトリ直下/shared/quizzes.ndjson を返す
        public static string GetShareFilePath() {
            string root = FindRepoRoot(AppDomain.CurrentDomain.BaseDirectory);
            if (string.IsNullOrWhiteSpace(root)) {
                root = AppDomain.CurrentDomain.BaseDirectory;
            }

            string dir = Path.Combine(root, FolderName);
            Directory.CreateDirectory(dir);

            return Path.Combine(dir, FileName);
        }

        // ★.git が「フォルダ」でも「ファイル」でもOK、.sln でもルート判定
        private static string FindRepoRoot(string startDir) {
            try {
                DirectoryInfo d = new DirectoryInfo(startDir);
                while (d != null) {
                    string git = Path.Combine(d.FullName, ".git");
                    if (Directory.Exists(git) || File.Exists(git))
                        return d.FullName;

                    FileInfo[] sln = d.GetFiles("*.sln");
                    if (sln != null && sln.Length > 0)
                        return d.FullName;

                    d = d.Parent;
                }
            }
            catch { }
            return "";
        }

        // =========================
        // ★安定キー生成（WorkKey/Type/Question/Choices）
        // =========================
        private static string ComputeKey(string workKey, int type, string question, IEnumerable<SharedChoiceDto> choices) {
            workKey = (workKey ?? "").Trim();
            question = (question ?? "").Trim();

            var cs = (choices ?? Enumerable.Empty<SharedChoiceDto>())
                .Take(3)
                .Select(c => ((c?.Text ?? "").Trim()) + ":" + ((c != null && c.IsCorrect) ? "1" : "0"))
                .ToArray();

            string raw = workKey + "|" + type.ToString() + "|" + question + "|" + string.Join("|", cs);

            using (var sha = SHA256.Create()) {
                byte[] bytes = Encoding.UTF8.GetBytes(raw);
                byte[] hash = sha.ComputeHash(bytes);

                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static string ComputeKey(string workKey, int type, string question, IEnumerable<Choice> choices) {
            var dto = (choices ?? Enumerable.Empty<Choice>())
                .OrderBy(c => c.ChoiceId)
                .Take(3)
                .Select(c => new SharedChoiceDto { Text = c.Text, IsCorrect = c.IsCorrect });
            return ComputeKey(workKey, type, question, dto);
        }

        private static string GetDtoKey(SharedQuizDto dto) {
            if (dto == null) return "";
            if (!string.IsNullOrWhiteSpace(dto.Key)) return dto.Key;

            string wk = dto.Work != null ? dto.Work.WorkKey : "";
            return ComputeKey(wk, dto.Type, dto.Question, dto.Choices);
        }

        // =========================
        // 保存時：共有ファイルへ追記（upsert）
        // =========================
        public static async Task AppendAsync(Work work, Quiz quiz, List<Choice> choices) {
            if (work == null || quiz == null || choices == null) return;

            var dtoChoices = choices.Select(c => new SharedChoiceDto {
                Text = c.Text,
                IsCorrect = c.IsCorrect
            }).ToList();

            SharedQuizDto dto = new SharedQuizDto {
                Action = "upsert",
                Key = ComputeKey(work.WorkKey, quiz.Type, quiz.Question, dtoChoices),
                Work = new SharedWorkDto {
                    WorkKey = work.WorkKey,
                    TmdbId = work.TmdbId,
                    MediaType = work.MediaType,
                    Title = work.Title,
                    Overview = work.Overview,
                    PosterPath = work.PosterPath,
                    BackdropPath = work.BackdropPath,
                    ReleaseDate = work.ReleaseDate
                },
                Type = quiz.Type,
                Question = quiz.Question,
                CreatedBy = quiz.CreatedBy,
                CreatedAt = quiz.CreatedAt,
                Choices = dtoChoices
            };

            await AppendLineAsync(dto);
        }

        // =========================
        // ★削除時：共有ファイルへ追記（delete）
        // =========================
        public static async Task AppendDeleteAsync(Work work, Quiz quiz, List<Choice> choices) {
            if (quiz == null) return;

            string wk = (work != null ? work.WorkKey : (quiz.WorkKey ?? "")) ?? "";
            wk = wk.Trim();
            if (wk.Length == 0) return;

            var dtoChoices = (choices ?? new List<Choice>())
                .OrderBy(c => c.ChoiceId)
                .Take(3)
                .Select(c => new SharedChoiceDto { Text = c.Text, IsCorrect = c.IsCorrect })
                .ToList();

            SharedQuizDto dto = new SharedQuizDto {
                Action = "delete",
                Key = ComputeKey(wk, quiz.Type, quiz.Question, dtoChoices),
                Work = new SharedWorkDto {
                    WorkKey = wk,
                    TmdbId = work != null ? work.TmdbId : 0,
                    MediaType = work != null ? work.MediaType : "",
                    Title = work != null ? work.Title : "",
                    Overview = work != null ? work.Overview : "",
                    PosterPath = work != null ? work.PosterPath : "",
                    BackdropPath = work != null ? work.BackdropPath : "",
                    ReleaseDate = work != null ? work.ReleaseDate : ""
                },
                Type = quiz.Type,
                Question = quiz.Question ?? "",
                CreatedBy = quiz.CreatedBy ?? "",
                CreatedAt = quiz.CreatedAt ?? "",
                Choices = dtoChoices
            };

            await AppendLineAsync(dto);
        }

        private static async Task AppendLineAsync(SharedQuizDto dto) {
            string line = JsonConvert.SerializeObject(dto, Formatting.None);
            string path = GetShareFilePath();

            using (FileStream fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (StreamWriter sw = new StreamWriter(fs, new UTF8Encoding(false))) {
                await sw.WriteLineAsync(line);
            }
        }

        // =========================
        // 共有ファイル → DBへ取り込み（返り値=今回新規に取り込んだ件数）
        //  - upsert: 既存ならスキップ
        //  - delete: 一致するクイズがあればDBから削除
        // =========================
        public static async Task<int> ImportToDbAsync() {
            await AppDb.InitAsync();

            string path = GetShareFilePath();
            if (!File.Exists(path)) return 0;

            string[] lines;
            try {
                lines = File.ReadAllLines(path, new UTF8Encoding(false));
            }
            catch {
                return 0;
            }

            int imported = 0;

            for (int i = 0; i < lines.Length; i++) {
                string raw = lines[i];
                string line = (raw ?? "").Trim();
                if (line.Length == 0) continue;

                // Gitコンフリクト記号行は無視
                if (line.StartsWith("<<<<<<<") || line.StartsWith("=======") || line.StartsWith(">>>>>>>"))
                    continue;

                SharedQuizDto dto;
                try {
                    dto = JsonConvert.DeserializeObject<SharedQuizDto>(line);
                }
                catch {
                    continue;
                }

                if (dto == null || dto.Work == null) continue;
                if (string.IsNullOrWhiteSpace(dto.Work.WorkKey)) continue;

                string action = (dto.Action ?? "").Trim().ToLowerInvariant();
                if (action.Length == 0) action = "upsert"; // 後方互換

                if (action == "delete") {
                    await ApplyDeleteAsync(dto);
                    continue;
                }

                // upsert は最低限チェック
                if (string.IsNullOrWhiteSpace(dto.Question)) continue;
                if (dto.Choices == null || dto.Choices.Count == 0) continue;

                // Work を先に入れる
                try {
                    Work w = new Work {
                        WorkKey = dto.Work.WorkKey,
                        TmdbId = dto.Work.TmdbId,
                        MediaType = dto.Work.MediaType,
                        Title = dto.Work.Title,
                        Overview = dto.Work.Overview,
                        PosterPath = dto.Work.PosterPath,
                        BackdropPath = dto.Work.BackdropPath,
                        ReleaseDate = dto.Work.ReleaseDate
                    };
                    await AppDb.Connection.InsertOrReplaceAsync(w);
                }
                catch { }

                // 既に同一クイズがあるならスキップ
                bool exists = await ExistsSameQuizAsync(dto);
                if (exists) continue;

                // Insert Quiz + Choices
                try {
                    Quiz quiz = new Quiz {
                        WorkKey = dto.Work.WorkKey,
                        Type = dto.Type,
                        Question = dto.Question,
                        CreatedBy = dto.CreatedBy ?? "",
                        CreatedAt = dto.CreatedAt ?? ""
                    };

                    await AppDb.Connection.InsertAsync(quiz);

                    if (quiz.QuizId <= 0) {
                        quiz.QuizId = await AppDb.Connection.ExecuteScalarAsync<int>("select last_insert_rowid()");
                    }

                    for (int c = 0; c < dto.Choices.Count && c < 3; c++) {
                        SharedChoiceDto ch = dto.Choices[c];
                        await AppDb.Connection.InsertAsync(new Choice {
                            QuizId = quiz.QuizId,
                            Text = ch.Text ?? "",
                            IsCorrect = ch.IsCorrect
                        });
                    }

                    imported++;
                }
                catch {
                    // 1件失敗しても続行
                }
            }

            return imported;
        }

        // =========================
        // ★delete 適用：一致するクイズをDBから削除
        // =========================
        private static async Task ApplyDeleteAsync(SharedQuizDto dto) {
            try {
                string wk = dto.Work != null ? (dto.Work.WorkKey ?? "") : "";
                wk = wk.Trim();
                if (wk.Length == 0) return;

                string targetKey = GetDtoKey(dto);
                if (targetKey.Length == 0) return;

                // WorkKey + Type で候補を絞る（Question一致に寄せてもOK）
                List<Quiz> candidates = await AppDb.Connection.Table<Quiz>()
                    .Where(q => q.WorkKey == wk && q.Type == dto.Type)
                    .ToListAsync();

                if (candidates == null || candidates.Count == 0) return;

                for (int i = 0; i < candidates.Count; i++) {
                    Quiz q = candidates[i];
                    if (q == null) continue;

                    // キー一致を確認（choices込み）
                    List<Choice> dbChoices = await AppDb.Connection.Table<Choice>()
                        .Where(c => c.QuizId == q.QuizId)
                        .ToListAsync();

                    string k = ComputeKey(wk, q.Type, q.Question, dbChoices ?? new List<Choice>());
                    if (!string.Equals(k, targetKey, StringComparison.Ordinal)) continue;

                    // ★削除（Choice/Play/Quiz）
                    await AppDb.Connection.ExecuteAsync("DELETE FROM [Choice] WHERE QuizId = ?", q.QuizId);
                    await AppDb.Connection.ExecuteAsync("DELETE FROM [Play]   WHERE QuizId = ?", q.QuizId);
                    await AppDb.Connection.ExecuteAsync("DELETE FROM [Quiz]   WHERE QuizId = ?", q.QuizId);
                }
            }
            catch {
                // delete は失敗しても続行
            }
        }

        private static async Task<bool> ExistsSameQuizAsync(SharedQuizDto dto) {
            try {
                string wk = dto.Work != null ? (dto.Work.WorkKey ?? "") : "";
                wk = wk.Trim();
                if (wk.Length == 0) return false;

                string targetKey = GetDtoKey(dto);

                // まず WorkKey + Type + Question で候補を狭める
                List<Quiz> candidates = await AppDb.Connection.Table<Quiz>()
                    .Where(q => q.WorkKey == wk && q.Type == dto.Type && q.Question == dto.Question)
                    .ToListAsync();

                if (candidates == null || candidates.Count == 0) return false;

                for (int i = 0; i < candidates.Count; i++) {
                    Quiz q = candidates[i];

                    List<Choice> dbChoices = await AppDb.Connection.Table<Choice>()
                        .Where(c => c.QuizId == q.QuizId)
                        .ToListAsync();

                    // ★キーが使えるならキーで判定（順序/ID差を吸収）
                    if (!string.IsNullOrWhiteSpace(targetKey)) {
                        string k = ComputeKey(wk, q.Type, q.Question, dbChoices ?? new List<Choice>());
                        if (string.Equals(k, targetKey, StringComparison.Ordinal)) return true;
                    } else {
                        // 旧方式（保険）
                        if (SameChoices(dbChoices ?? new List<Choice>(), dto.Choices ?? new List<SharedChoiceDto>()))
                            return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private static bool SameChoices(List<Choice> db, List<SharedChoiceDto> file) {
            List<Choice> a = db.OrderBy(x => x.ChoiceId).Take(3).ToList();
            List<SharedChoiceDto> b = file.Take(3).ToList();
            if (a.Count != b.Count) return false;

            for (int i = 0; i < a.Count; i++) {
                string at = (a[i].Text ?? "").Trim();
                string bt = (b[i].Text ?? "").Trim();
                if (!string.Equals(at, bt, StringComparison.Ordinal)) return false;
                if (a[i].IsCorrect != b[i].IsCorrect) return false;
            }
            return true;
        }
    }
}

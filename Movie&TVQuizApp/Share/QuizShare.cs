using Movie_AnimeQuizApp.Data;
using Movie_AnimeQuizApp.Data.Entities;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Movie_AnimeQuizApp.Share {
    // 1行=1クイズ のJSON（NDJSON）でGit共有する
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
            public SharedWorkDto Work { get; set; }
            public int Type { get; set; }           // 1固定
            public string Question { get; set; }
            public string CreatedBy { get; set; }
            public string CreatedAt { get; set; }
            public List<SharedChoiceDto> Choices { get; set; }
        }

        // リポジトリ直下/shared/quizzes.ndjson を見つける
        public static string GetShareFilePath() {
            string root = FindRepoRoot(AppDomain.CurrentDomain.BaseDirectory);
            if (string.IsNullOrWhiteSpace(root)) {
                // .git が見つからない場合はプロジェクト配下に fallback（VS実行想定）
                root = AppDomain.CurrentDomain.BaseDirectory;
            }

            string dir = Path.Combine(root, FolderName);
            Directory.CreateDirectory(dir);

            return Path.Combine(dir, FileName);
        }

        private static string FindRepoRoot(string startDir) {
            try {
                var d = new DirectoryInfo(startDir);
                while (d != null) {
                    // ★.git が「フォルダ」でも「ファイル」でもOKにする
                    string gitPath = Path.Combine(d.FullName, ".git");
                    if (Directory.Exists(gitPath) || File.Exists(gitPath))
                        return d.FullName;

                    // ★保険：.sln があればそこをルート扱い
                    if (d.EnumerateFiles("*.sln").Any())
                        return d.FullName;

                    d = d.Parent;
                }
            }
            catch { }
            return "";
        }


        // 保存時：共有ファイルへ追記（DB保存とは別。失敗してもDBは保存済み）
        public static async Task AppendAsync(Work work, Quiz quiz, List<Choice> choices) {
            if (work == null || quiz == null || choices == null) return;

            var dto = new SharedQuizDto {
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
                Choices = choices.Select(c => new SharedChoiceDto {
                    Text = c.Text,
                    IsCorrect = c.IsCorrect
                }).ToList()
            };

            string line = JsonConvert.SerializeObject(dto, Formatting.None);
            string path = GetShareFilePath();

            // 1行追記（改行区切り）
            using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var sw = new StreamWriter(fs, new UTF8Encoding(false))) {
                await sw.WriteLineAsync(line);
            }
        }

        // 共有ファイル → DBへ取り込み（重複はスキップ）
        public static async Task ImportToDbAsync() {
            await AppDb.InitAsync();

            string path = GetShareFilePath();
            if (!File.Exists(path)) return;

            string[] lines;
            try {
                lines = File.ReadAllLines(path, new UTF8Encoding(false));
            }
            catch {
                return;
            }

            foreach (var raw in lines) {
                var line = (raw ?? "").Trim();
                if (line.Length == 0) continue;

                // Gitコンフリクト文字を無視（解決は人手）
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
                if (string.IsNullOrWhiteSpace(dto.Question)) continue;
                if (dto.Choices == null || dto.Choices.Count == 0) continue;

                // Work を先に入れる（相手PCにWorkが無いとQuizPlayWindowが開けない）
                try {
                    var w = new Work {
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

                // 既に同じクイズがDBにあるか判定（WorkKey + Type + Question + choices一致）
                bool exists = await ExistsSameQuizAsync(dto);
                if (exists) continue;

                // Insert
                try {
                    var quiz = new Quiz {
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

                    foreach (var c in dto.Choices.Take(3)) {
                        await AppDb.Connection.InsertAsync(new Choice {
                            QuizId = quiz.QuizId,
                            Text = c.Text ?? "",
                            IsCorrect = c.IsCorrect
                        });
                    }
                }
                catch {
                    // 1件失敗しても続行
                }
            }
        }

        private static async Task<bool> ExistsSameQuizAsync(SharedQuizDto dto) {
            try {
                var candidates = await AppDb.Connection.Table<Quiz>()
                    .Where(q => q.WorkKey == dto.Work.WorkKey && q.Type == dto.Type && q.Question == dto.Question)
                    .ToListAsync();

                if (candidates == null || candidates.Count == 0) return false;

                // choices一致チェック
                foreach (var q in candidates) {
                    var dbChoices = await AppDb.Connection.Table<Choice>()
                        .Where(c => c.QuizId == q.QuizId)
                        .ToListAsync();

                    if (dbChoices == null) continue;

                    if (SameChoices(dbChoices, dto.Choices))
                        return true;
                }
            }
            catch { }

            return false;
        }

        private static bool SameChoices(List<Choice> db, List<SharedChoiceDto> file) {
            // 順番込みで比較（あなたのUIは3つ固定なのでこれで十分）
            var a = db.OrderBy(x => x.ChoiceId).Take(3).ToList();
            var b = file.Take(3).ToList();
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

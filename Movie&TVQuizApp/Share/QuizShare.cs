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
    // 1行=1クイズ のJSON（NDJSON）でGit共有
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
                // fallback（最悪）
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

                    // .sln があればそこをルート扱い
                    FileInfo[] sln = d.GetFiles("*.sln");
                    if (sln != null && sln.Length > 0)
                        return d.FullName;

                    d = d.Parent;
                }
            }
            catch { }
            return "";
        }

        // 保存時：共有ファイルへ追記
        public static async Task AppendAsync(Work work, Quiz quiz, List<Choice> choices) {
            if (work == null || quiz == null || choices == null) return;

            SharedQuizDto dto = new SharedQuizDto {
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

            using (FileStream fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (StreamWriter sw = new StreamWriter(fs, new UTF8Encoding(false))) {
                await sw.WriteLineAsync(line);
            }
        }

        // 共有ファイル → DBへ取り込み（返り値=今回新規に取り込んだ件数）
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

        private static async Task<bool> ExistsSameQuizAsync(SharedQuizDto dto) {
            try {
                List<Quiz> candidates = await AppDb.Connection.Table<Quiz>()
                    .Where(q => q.WorkKey == dto.Work.WorkKey && q.Type == dto.Type && q.Question == dto.Question)
                    .ToListAsync();

                if (candidates == null || candidates.Count == 0) return false;

                for (int i = 0; i < candidates.Count; i++) {
                    Quiz q = candidates[i];
                    List<Choice> dbChoices = await AppDb.Connection.Table<Choice>()
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

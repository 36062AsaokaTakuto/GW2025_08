using SQLite;

namespace Movie_AnimeQuizApp.Data.Entities {
    public class Quiz {
        [PrimaryKey, AutoIncrement]
        public int QuizId { get; set; }

        [Indexed]
        public string WorkKey { get; set; } // Work.WorkKey を参照

        public int Type { get; set; } // 0=概要, 1=中身
        public string Question { get; set; }
        public string CreatedBy { get; set; }
        public string CreatedAt { get; set; }
    }
}
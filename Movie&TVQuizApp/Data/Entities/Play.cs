using SQLite;

namespace Movie_AnimeQuizApp.Data.Entities {
    public class Play {
        [PrimaryKey, AutoIncrement]
        public int PlayId { get; set; }

        [Indexed]
        public int QuizId { get; set; }

        public string User { get; set; }
        public bool IsCorrect { get; set; }
        public string PlayedAt { get; set; }
    }
}
using SQLite;

namespace Movie_AnimeQuizApp.Data.Entities {
    public class Choice {
        [PrimaryKey, AutoIncrement]
        public int ChoiceId { get; set; }

        [Indexed]
        public int QuizId { get; set; }

        public string Text { get; set; }
        public bool IsCorrect { get; set; }
    }
}
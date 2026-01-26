using SQLite;

namespace Movie_AnimeQuizApp.Data.Entities {
    public class Work {
        [PrimaryKey]
        public string WorkKey { get; set; } // "movie:123" / "tv:456"

        [Indexed]
        public int TmdbId { get; set; }

        [Indexed]
        public string MediaType { get; set; } // "movie" / "tv"

        public string Title { get; set; }
        public string Overview { get; set; }
        public string PosterPath { get; set; }
        public string BackdropPath { get; set; }
        public string ReleaseDate { get; set; }
    }
}
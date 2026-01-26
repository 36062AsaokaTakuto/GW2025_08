using SQLite;
using SQLitePCL;
using System;
using System.IO;
using System.Threading.Tasks;
using Movie_AnimeQuizApp.Data.Entities;

namespace Movie_AnimeQuizApp.Data {
    public static class AppDb {
        private static bool _inited;
        public static SQLiteAsyncConnection Connection { get; private set; }

        public static async Task InitAsync() {
            if (_inited) return;

            // e_sqlite3.dll を読み込む（bundle_green 必須）
            Batteries_V2.Init();

            var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.db3");
            Connection = new SQLiteAsyncConnection(dbPath);

            await Connection.CreateTableAsync<Work>();
            await Connection.CreateTableAsync<Quiz>();
            await Connection.CreateTableAsync<Choice>();
            await Connection.CreateTableAsync<Play>();

            _inited = true;
        }
    }
}
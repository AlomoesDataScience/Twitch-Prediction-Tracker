using Microsoft.Data.Sqlite;

namespace PredictionBot;

record OutcomeRow(string Id, int Position, string Title, long Points, long Users);

record PredictionRow(string Id, string Channel, string Title, string Status,
                     string? WinningOutcomeId, string UpdatedAt, List<OutcomeRow> Outcomes);

class Db
{
    readonly string _connectionString;
    readonly object _gate = new();

    public Db(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var con = Open();
        Exec(con, """
            CREATE TABLE IF NOT EXISTS predictions (
                id                 TEXT PRIMARY KEY,
                channel_id         TEXT,
                channel            TEXT NOT NULL DEFAULT '',
                title              TEXT NOT NULL DEFAULT '',
                status             TEXT NOT NULL,
                created_at         TEXT,
                locked_at          TEXT,
                ended_at           TEXT,
                winning_outcome_id TEXT,
                updated_at         TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS outcomes (
                id            TEXT PRIMARY KEY,
                prediction_id TEXT NOT NULL REFERENCES predictions(id),
                position      INTEGER NOT NULL,
                title         TEXT NOT NULL DEFAULT '',
                color         TEXT,
                points        INTEGER NOT NULL DEFAULT 0,
                users         INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS outcomes_prediction ON outcomes(prediction_id);
            """);
    }

    SqliteConnection Open()
    {
        var con = new SqliteConnection(_connectionString);
        con.Open();
        return con;
    }

    static void Exec(SqliteConnection con, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void Upsert(PredictionEvent e, string channel)
    {
        lock (_gate)
        {
            using var con = Open();
            using var tx = con.BeginTransaction();

            // Once a prediction is RESOLVED it is final: a late or stale update must not reopen it.
            Exec(con, """
                INSERT INTO predictions (id, channel_id, channel, title, status, created_at, locked_at,
                                         ended_at, winning_outcome_id, updated_at)
                VALUES ($id, $channel_id, $channel, $title, $status, $created, $locked, $ended, $winner, $now)
                ON CONFLICT(id) DO UPDATE SET
                    channel_id         = COALESCE(excluded.channel_id, channel_id),
                    channel            = CASE WHEN channel = '' THEN excluded.channel ELSE channel END,
                    title              = excluded.title,
                    status             = CASE WHEN status = 'RESOLVED' THEN status ELSE excluded.status END,
                    created_at         = COALESCE(excluded.created_at, created_at),
                    locked_at          = COALESCE(excluded.locked_at, locked_at),
                    ended_at           = COALESCE(excluded.ended_at, ended_at),
                    winning_outcome_id = COALESCE(excluded.winning_outcome_id, winning_outcome_id),
                    updated_at         = excluded.updated_at
                """,
                ("$id", e.Id), ("$channel_id", e.ChannelId), ("$channel", channel), ("$title", e.Title),
                ("$status", e.Status), ("$created", e.CreatedAt), ("$locked", e.LockedAt),
                ("$ended", e.EndedAt), ("$winner", e.WinningOutcomeId), ("$now", DateTime.UtcNow.ToString("o")));

            for (var i = 0; i < e.Outcomes.Count; i++)
            {
                var o = e.Outcomes[i];
                Exec(con, """
                    INSERT INTO outcomes (id, prediction_id, position, title, color, points, users)
                    VALUES ($id, $prediction, $position, $title, $color, $points, $users)
                    ON CONFLICT(id) DO UPDATE SET
                        title = excluded.title, color = excluded.color,
                        points = excluded.points, users = excluded.users
                    """,
                    ("$id", o.Id), ("$prediction", e.Id), ("$position", i), ("$title", o.Title),
                    ("$color", o.Color), ("$points", o.Points), ("$users", o.Users));
            }

            tx.Commit();
        }
    }

    public List<PredictionRow> Load()
    {
        lock (_gate)
        {
            using var con = Open();

            var outcomes = new Dictionary<string, List<OutcomeRow>>();
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "SELECT prediction_id, id, position, title, points, users FROM outcomes ORDER BY position";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!outcomes.TryGetValue(r.GetString(0), out var list)) outcomes[r.GetString(0)] = list = new();
                    list.Add(new OutcomeRow(r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5)));
                }
            }

            var result = new List<PredictionRow>();
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "SELECT id, channel, title, status, winning_outcome_id, updated_at FROM predictions ORDER BY updated_at";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    result.Add(new PredictionRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                        r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5),
                        outcomes.GetValueOrDefault(r.GetString(0)) ?? new()));
            }
            return result;
        }
    }
}

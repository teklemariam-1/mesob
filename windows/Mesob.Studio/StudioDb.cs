using Microsoft.Data.Sqlite;

namespace Mesob.Studio;

/// <summary>Studio Control's local database. The Studio PC's disk is BitLocker-encrypted; secrets
/// inside are additionally locked with <see cref="Mesob.Interop.SecureStore"/>.</summary>
public sealed class StudioDb : IDisposable
{
    private readonly SqliteConnection _c;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT);
        CREATE TABLE IF NOT EXISTS capacity(block_id INTEGER PRIMARY KEY, record TEXT NOT NULL,
            serial_start INTEGER NOT NULL, serial_count INTEGER NOT NULL, next_serial INTEGER NOT NULL, added TEXT);
        CREATE TABLE IF NOT EXISTS titles(title_id TEXT PRIMARY KEY, title_en TEXT, title_ti TEXT, artist TEXT,
            kind TEXT, mime TEXT, size INTEGER, price INTEGER, file TEXT, key_locked TEXT, active INTEGER DEFAULT 1, created TEXT);
        CREATE TABLE IF NOT EXISTS shops(shop_id INTEGER PRIMARY KEY, name TEXT, town TEXT, phone TEXT,
            sign_public TEXT, kex_public TEXT, cert TEXT, secret_locked TEXT, setup_request TEXT, blocked INTEGER DEFAULT 0,
            log_next_seq INTEGER DEFAULT 0, created TEXT);
        CREATE TABLE IF NOT EXISTS packs(pack_id INTEGER PRIMARY KEY, shop_id INTEGER, block_id INTEGER,
            serial_start INTEGER, serial_count INTEGER, record TEXT, wholesale INTEGER, unlocked INTEGER DEFAULT 0, created TEXT);
        CREATE TABLE IF NOT EXISTS payments(id INTEGER PRIMARY KEY AUTOINCREMENT, shop_id INTEGER, amount INTEGER, note TEXT, created TEXT);
        CREATE TABLE IF NOT EXISTS sales(shop_id INTEGER, seq INTEGER, kind TEXT, serial INTEGER, title_id TEXT,
            device TEXT, price INTEGER, local_time INTEGER, hash TEXT, imported TEXT, PRIMARY KEY(shop_id, seq));
        CREATE INDEX IF NOT EXISTS sales_serial ON sales(serial);
        CREATE TABLE IF NOT EXISTS reports(shop_id INTEGER, day INTEGER, count INTEGER, cash INTEGER, log_tag TEXT,
            received TEXT, PRIMARY KEY(shop_id, day));
        CREATE TABLE IF NOT EXISTS alerts(id INTEGER PRIMARY KEY AUTOINCREMENT, created TEXT, text TEXT, UNIQUE(text));
        """;

    public StudioDb(string path)
    {
        _c = new SqliteConnection($"Data Source={path}");
        _c.Open();
        Exec(Schema);
    }

    private SqliteCommand Cmd(string sql, object?[] args)
    {
        var cmd = _c.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$" + (i + 1), args[i] ?? DBNull.Value);
        return cmd;
    }

    /// <summary>Run a statement. Parameters are $1, $2, … in order.</summary>
    public int Exec(string sql, params object?[] args)
    {
        using var cmd = Cmd(sql, args);
        return cmd.ExecuteNonQuery();
    }

    public object? Scalar(string sql, params object?[] args)
    {
        using var cmd = Cmd(sql, args);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    public long Long(string sql, params object?[] args) => Convert.ToInt64(Scalar(sql, args) ?? 0L);

    public List<Dictionary<string, object?>> Rows(string sql, params object?[] args)
    {
        using var cmd = Cmd(sql, args);
        using var r = cmd.ExecuteReader();
        var list = new List<Dictionary<string, object?>>();
        while (r.Read())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            list.Add(row);
        }
        return list;
    }

    public string? Get(string key) => Scalar("SELECT value FROM settings WHERE key=$1", key) as string;
    public void Set(string key, string value) => Exec("INSERT INTO settings(key,value) VALUES($1,$2) ON CONFLICT(key) DO UPDATE SET value=$2", key, value);

    public SqliteTransaction Begin() => _c.BeginTransaction();

    public void Alert(string text) => Exec("INSERT OR IGNORE INTO alerts(created,text) VALUES($1,$2)", Studio.Now(), text);

    public void Dispose() => _c.Dispose();
}

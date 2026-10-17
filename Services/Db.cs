using Microsoft.Data.Sqlite;

namespace Gallery.Services;

/// <summary>
/// SQLite 连接（galleryold app/store.py 同参数）：
/// busy_timeout=10000 / journal_mode=WAL / synchronous=NORMAL / foreign_keys=ON，
/// 并注册 <c>GALLERY_NATURAL</c> 自定义排序（等价 name_sort.compare_names）。
/// 红线：不注册该 collation，含 <c>COLLATE GALLERY_NATURAL</c> 的查询会直接报错。
/// </summary>
public sealed class Db : IDisposable
{
    public SqliteConnection Connection { get; }
    public string DbPath { get; }

    public Db(string dbPath)
    {
        DbPath = dbPath;
        Connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false, // collation 按连接注册；池化会换连接，禁用
        }.ToString());
        Connection.Open();
        Connection.CreateCollation("GALLERY_NATURAL", (a, b) => NaturalSort.CompareNames(a ?? "", b ?? ""));

        Exec("PRAGMA busy_timeout=10000");
        Exec("PRAGMA journal_mode=WAL");
        Exec("PRAGMA synchronous=NORMAL");
        Exec("PRAGMA foreign_keys=ON");
    }

    public int SchemaVersion
    {
        get
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key='schema_version'";
            var v = cmd.ExecuteScalar() as string;
            return int.TryParse(v, out var n) ? n : 0;
        }
    }

    public void Exec(string sql)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => Connection.Dispose();
}

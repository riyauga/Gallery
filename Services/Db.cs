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

    public Db(string dbPath, bool readOnly = false)
    {
        DbPath = dbPath;
        Connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false, // collation 按连接注册；池化会换连接，禁用
        }.ToString());
        Connection.Open();
        try
        {
            Connection.CreateCollation("GALLERY_NATURAL", (a, b) => NaturalSort.CompareNames(a ?? "", b ?? ""));
            Exec("PRAGMA busy_timeout=10000");
            if (readOnly) return;
            EnsureSchema();
            Exec("PRAGMA journal_mode=WAL");
            Exec("PRAGMA synchronous=NORMAL");
            Exec("PRAGMA foreign_keys=ON");
        }
        catch
        {
            Connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 空库直接建 v5；v3 旧库先备份升级到 v4，再增量升到 v5；
    /// 其他版本一律拒绝打开，避免误改用户数据。
    /// </summary>
    private void EnsureSchema()
    {
        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        if (Convert.ToInt32(command.ExecuteScalar()) == 0)
        {
            using var stream = typeof(Db).Assembly.GetManifestResourceStream("Gallery.Services.LibrarySchema.sql")
                ?? throw new InvalidOperationException("缺少新图库的数据结构资源。");
            using var reader = new StreamReader(stream);
            Exec("BEGIN IMMEDIATE");
            try
            {
                Exec(reader.ReadToEnd());
                Exec("COMMIT");
            }
            catch
            {
                Exec("ROLLBACK");
                throw;
            }
            return;
        }
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='meta'";
        if (Convert.ToInt32(command.ExecuteScalar()) == 0)
            throw new InvalidDataException("图库数据库缺少 meta 表，无法确认结构版本。");

        var version = SchemaVersion;
        if (version == 3)
        {
            LibraryMigrationV4.Upgrade(Connection, DbPath);
            version = SchemaVersion;
        }
        if (version == 4)
        {
            LibraryMigrationV5.Upgrade(Connection, DbPath);
            version = SchemaVersion;
        }
        if (version != LibraryMigrationV5.TargetVersion)
            throw new InvalidDataException(
                $"图库数据库版本不兼容（当前 v{version}，需要 v{LibraryMigrationV5.TargetVersion}），未修改原有数据结构。");
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

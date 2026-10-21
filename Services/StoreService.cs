using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace Gallery.Services;

public sealed record RootInfo(int Id, string Path, double AddedAt, bool Enabled);

public sealed record FolderCount(string DirRel, int Count);

public sealed record ImageRow(
    int Id, int RootId, string RelPath, string AbsPath, string DirRel,
    string Name, string Ext, long Size, double Mtime, double? Ctime,
    double? TakenAt, int? Width, int? Height, int Rating, bool Missing,
    double AddedAt, double UpdatedAt, string? OrigMd5, string? ContentMd5,
    string? PageUrl, string? SourceUrl, string? Nsfw, string? PostId,
    string? ImageUuid, string? FileKey, bool Managed)
{
    /// <summary>由 AttachTags 填充（批次 1 查询路径）。</summary>
    public List<(string Category, string Name)> Tags { get; set; } = [];
}

/// <summary>
/// 数据层，移植 galleryold <c>app/store.py</c>（批次 0 范围）：
/// roots / images / favorites / tags 读写、upsert（含移动识别）、
/// replace_roots 事务、sidecar 写入与导入、mark_missing。
/// 兼容红线见迁移计划 §5。
/// </summary>
public sealed class StoreService : IDisposable
{
    public const string SidecarName = ".gallery.json";
    public const string SidecarSchema = "gallery.sidecar/2";

    private readonly SqliteConnection _conn;
    private int _inTransaction;
    private bool _rollbackOnly;

    /// <summary>所有仓储入口及跨入口事务共用的可重入锁，禁止在事务内跨线程 await。</summary>
    public object SyncRoot => this;

    public string DbPath { get; }
    public bool SidecarEnabled { get; set; } = true;
    public bool HideSidecar { get; set; } = true;
    public List<string> TrackingNotes { get; } = [];
    public HashSet<(int RootId, string DirRel)> TrackingDirty { get; } = [];
    public string LastSidecarError { get; set; } = "";

    public StoreService(string dbPath, bool readOnly = false)
    {
        DbPath = dbPath;
        var db = new Db(dbPath, readOnly);
        _conn = db.Connection;
    }

    public int SchemaVersion
    {
        [MethodImpl(MethodImplOptions.Synchronized)]
        get
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key='schema_version'";
            return int.TryParse(cmd.ExecuteScalar() as string, out var n) ? n : 0;
        }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>自动提交模式下语句即时持久化；保留方法名与 galleryold 对齐。</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void Commit() { }

    /// <summary>
    /// 对应 store.py transaction()：外层 BEGIN IMMEDIATE，可嵌套；
    /// 成功置 <see cref="TxScope.Success"/>，异常时 Dispose 回滚（对齐 Python with 语义）。
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public TxScope Transaction() => new(this);

    public sealed class TxScope : IDisposable
    {
        private readonly StoreService _store;
        private bool _disposed;
        public bool Success { get; set; }

        internal TxScope(StoreService store)
        {
            _store = store;
            Monitor.Enter(store.SyncRoot);
            try
            {
                if (store._inTransaction == 0)
                {
                    store.Exec("BEGIN IMMEDIATE");
                    store._rollbackOnly = false;
                }
                store._inTransaction++;
            }
            catch
            {
                Monitor.Exit(store.SyncRoot);
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!Success) _store._rollbackOnly = true;
                if (--_store._inTransaction == 0)
                {
                    _store.Exec(_store._rollbackOnly ? "ROLLBACK" : "COMMIT");
                    if (Success && _store._rollbackOnly)
                        throw new InvalidOperationException("嵌套事务失败，整笔操作已回滚。");
                }
            }
            finally { Monitor.Exit(_store.SyncRoot); }
        }
    }

    // ------------------------------------------------------------------ roots

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<RootInfo> Roots()
    {
        var list = new List<RootInfo>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id,path,added_at,enabled FROM roots ORDER BY id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new RootInfo(r.GetInt32(0), r.GetString(1), r.GetDouble(2), r.GetInt64(3) != 0));
        return list;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public string? RootPath(int rootId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT path FROM roots WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", rootId);
        var v = cmd.ExecuteScalar() as string;
        return v is null ? null : Path.GetFullPath(v);
    }

    /// <summary>对应 root_changes.import_relation：重复/父含子/子含父 → 整笔拒绝。</summary>
    public static (string? Duplicate, string? Parent, List<string> Children) ImportRelation(
        string path, IEnumerable<string> roots)
    {
        var target = FileIdentity.PathKey(path);
        string? duplicate = null, parent = null;
        var children = new List<string>();
        foreach (var root in roots)
        {
            var key = FileIdentity.PathKey(root);
            if (key == target) duplicate = root;
            else if (IsUnder(target, key)) parent = root;
            else if (IsUnder(key, target)) children.Add(root);
        }
        return (duplicate, parent, children);
    }

    /// <summary>child 是否位于 parent 之下（Path.is_relative_to 语义，含相等）。</summary>
    private static bool IsUnder(string child, string parent)
    {
        if (child == parent) return true;
        return child.StartsWith(parent.EndsWith('\\') ? parent : parent + '\\',
            StringComparison.OrdinalIgnoreCase);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public int AddRoot(string path)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO roots(path,added_at) VALUES($p,$t); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$p", Path.GetFullPath(path));
        cmd.Parameters.AddWithValue("$t", Now());
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// 对应 store.py replace_roots：整体更新启用根；子根记录原地迁移；
    /// 任何 rel_path 冲突即抛异常整笔回滚。
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public Dictionary<string, int> ReplaceRoots(IEnumerable<string> paths)
    {
        var canonical = paths.Select(p => Path.GetFullPath(p))
            .Distinct().ToList();
        foreach (var path in canonical)
        {
            var (dup, parent, children) = ImportRelation(path,
                canonical.Where(p => !string.Equals(FileIdentity.PathKey(p), FileIdentity.PathKey(path), StringComparison.Ordinal)));
            if (dup is not null || parent is not null || children.Count > 0)
                throw new InvalidOperationException("图库根目录不能重复或互相包含。");
        }

        using var tx = Transaction();
        var rows = new List<(int Id, string Path, bool Enabled)>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id,path,enabled FROM roots";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetInt32(0), r.GetString(1), r.GetInt64(2) != 0));
        }
        var destinations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in canonical)
        {
            var row = rows.FirstOrDefault(r =>
                FileIdentity.PathKey(r.Path) == FileIdentity.PathKey(path));
            if (row == default)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "INSERT INTO roots(path,added_at) VALUES($p,$t); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$p", path);
                cmd.Parameters.AddWithValue("$t", Now());
                destinations[path] = Convert.ToInt32(cmd.ExecuteScalar());
            }
            else
            {
                destinations[path] = row.Id;
            }
        }
        foreach (var (path, targetId) in destinations)
        {
            foreach (var old in rows.Where(r => r.Enabled && r.Id != targetId))
            {
                if (!IsUnder(FileIdentity.PathKey(old.Path), FileIdentity.PathKey(path))) continue;
                var prefix = System.IO.Path.GetRelativePath(path, old.Path).Replace('\\', '/');
                using (var sel = _conn.CreateCommand())
                {
                    sel.CommandText = "SELECT id,rel_path,dir_rel FROM images WHERE root_id=$id";
                    sel.Parameters.AddWithValue("$id", old.Id);
                    using var r = sel.ExecuteReader();
                    var moved = new List<(int Id, string Rel, string Dir)>();
                    while (r.Read())
                        moved.Add((r.GetInt32(0), r.GetString(1), r.GetString(2)));
                    foreach (var image in moved)
                    {
                        var relative = prefix.Length > 0 ? $"{prefix}/{image.Rel}" : image.Rel;
                        using var chk = _conn.CreateCommand();
                        chk.CommandText = "SELECT id FROM images WHERE root_id=$t AND rel_path=$r COLLATE NOCASE";
                        chk.Parameters.AddWithValue("$t", targetId);
                        chk.Parameters.AddWithValue("$r", relative);
                        var conflict = chk.ExecuteScalar();
                        if (conflict is not null)
                            throw new InvalidOperationException(
                                $"历史索引冲突：{relative}（图片 {image.Id} 与 {conflict}）。未替换任何入口，请先核对历史记录。");
                        var directory = prefix.Length > 0
                            ? (image.Dir.Length > 0 ? $"{prefix}/{image.Dir}" : prefix)
                            : image.Dir;
                        using var upd = _conn.CreateCommand();
                        upd.CommandText = "UPDATE images SET root_id=$t,rel_path=$r,dir_rel=$d WHERE id=$i";
                        upd.Parameters.AddWithValue("$t", targetId);
                        upd.Parameters.AddWithValue("$r", relative);
                        upd.Parameters.AddWithValue("$d", directory);
                        upd.Parameters.AddWithValue("$i", image.Id);
                        upd.ExecuteNonQuery();
                    }
                }
            }
        }
        Exec("UPDATE roots SET enabled=0");
        foreach (var rid in destinations.Values)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE roots SET enabled=1 WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", rid);
            cmd.ExecuteNonQuery();
        }
        tx.Success = true;
        return destinations;
    }

    // ----------------------------------------------------------------- images

    /// <summary>对应 store.py upsert_image（移动识别 + 内容 md5 兜底）。</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public (int ImageId, bool Created) UpsertImage(
        int rootId, string relPath, string absPath, string dirRel,
        string name, string ext, long size, double mtime, double? ctime,
        int? width, int? height, double? takenAt, double? now = null)
    {
        var ts = now ?? Now();
        var key = FileIdentity.IdentityOf(absPath);
        ImageRow? row = QueryOne(
            "SELECT * FROM images WHERE root_id=$r AND rel_path=$p",
            ("$r", rootId), ("$p", relPath));

        // 1) 文件身份优先：同卷移动/改名后仍能认出
        if (key.Length > 0 && (row is null || row.FileKey != key))
        {
            var moved = QueryAll("SELECT * FROM images WHERE file_key=$k", ("$k", key));
            var missing = moved.Where(r => FileIdentity.IdentityOf(r.AbsPath) != key).ToList();
            if (missing.Count == 1)
            {
                var old = missing[0];
                if (row is not null && row.Id != old.Id)
                {
                    var retired = ".gallery-missing/" + (row.ImageUuid ?? Guid.NewGuid().ToString()) + "/" + row.Name;
                    Exec($"UPDATE images SET rel_path='{Esc(retired)}',missing=1 WHERE id={row.Id}");
                    if (row.Managed) TrackingDirty.Add((row.RootId, row.DirRel));
                }
                if (old.Managed) TrackingDirty.Add((old.RootId, old.DirRel));
                Exec($"UPDATE images SET root_id={rootId},rel_path='{Esc(relPath)}',abs_path='{Esc(absPath)}'," +
                     $"dir_rel='{Esc(dirRel)}',name='{Esc(name)}',ext='{Esc(ext)}',missing=0 WHERE id={old.Id}");
                row = old;
                if (old.Managed)
                    TrackingNotes.Add($"已保留移动或改名图片的标签：{name}");
            }
            else if (missing.Count > 1)
            {
                TrackingNotes.Add($"无法唯一匹配文件身份，请检查标签：{name}");
            }
        }

        // 2) 内容 md5 兜底（同尺寸且原位置已缺失的图）
        if (row is null)
        {
            var candidates = QueryAll(
                "SELECT * FROM images WHERE size=$s AND content_md5 IS NOT NULL AND content_md5!=''",
                ("$s", size))
                .Where(r => r.Missing || !File.Exists(r.AbsPath))
                .ToList();
            if (candidates.Count > 0)
            {
                var digest = FileIdentity.ContentMd5(absPath);
                var matches = candidates.Where(r => r.ContentMd5 == digest).ToList();
                if (matches.Count == 1)
                {
                    var old = matches[0];
                    if (old.Managed) TrackingDirty.Add((old.RootId, old.DirRel));
                    Exec($"UPDATE images SET root_id={rootId},rel_path='{Esc(relPath)}',abs_path='{Esc(absPath)}'," +
                         $"dir_rel='{Esc(dirRel)}',name='{Esc(name)}',ext='{Esc(ext)}',missing=0 WHERE id={old.Id}");
                    row = old;
                }
                else if (matches.Count > 1)
                {
                    TrackingNotes.Add($"无法唯一匹配移动的图片，请检查标签：{name}");
                }
            }
        }

        // 3) 新建
        if (row is null)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO images(root_id,rel_path,abs_path,dir_rel,name,ext,size," +
                "mtime,ctime,taken_at,width,height,added_at,updated_at,missing) " +
                "VALUES($r,$p,$a,$d,$n,$e,$s,$m,$c,$t,$w,$h,$at,$ut,0); SELECT last_insert_rowid();";
            Fill(cmd, rootId, relPath, absPath, dirRel, name, ext, size, mtime, ctime, takenAt, width, height, ts, ts);
            var id = Convert.ToInt32(cmd.ExecuteScalar());
            Exec($"UPDATE images SET image_uuid='{Guid.NewGuid()}',file_key='{Esc(key)}' WHERE id={id}");
            return (id, true);
        }

        // 4) 已存在：变化才更新体积类字段
        bool changed = row.Size != size || Math.Abs(row.Mtime - mtime) > 1e-6;
        if (changed)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "UPDATE images SET abs_path=$a,name=$n,ext=$e,size=$s,mtime=$m,ctime=$c," +
                "width=COALESCE($w,width),height=COALESCE($h,height)," +
                "taken_at=COALESCE(taken_at,$t),missing=0,updated_at=$ut WHERE id=$i";
            Fill(cmd, 0, null, absPath, null, name, ext, size, mtime, ctime, takenAt, width, height, null, ts, row.Id);
            cmd.ExecuteNonQuery();
        }
        else
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "UPDATE images SET abs_path=$a,missing=0," +
                "width=COALESCE(width,$w),height=COALESCE(height,$h)," +
                "taken_at=COALESCE(taken_at,$t),ctime=COALESCE(ctime,$c) WHERE id=$i";
            Fill(cmd, 0, null, absPath, null, null, null, 0, 0, ctime, takenAt, width, height, null, null, row.Id);
            cmd.ExecuteNonQuery();
        }
        Exec($"UPDATE images SET file_key='{Esc(key)}' WHERE id={row.Id}");
        if (changed)
        {
            var meta = QueryOne("SELECT managed,content_md5 FROM images WHERE id=$i", ("$i", row.Id));
            if (meta is not null && (meta.Managed || (meta.ContentMd5?.Length ?? 0) > 0))
                Exec($"UPDATE images SET content_md5='{Esc(FileIdentity.ContentMd5(absPath))}' WHERE id={row.Id}");
        }
        return (row.Id, false);
    }

    private static string Esc(string s) => s.Replace("'", "''");

    private static void Fill(SqliteCommand cmd, int rootId, string? relPath, string? absPath,
        string? dirRel, string? name, string? ext, long size, double mtime, double? ctime,
        double? takenAt, int? width, int? height, double? addedAt, double? updatedAt, int? id = null)
    {
        void Add(string name2, object? v)
        {
            if (v is null) cmd.Parameters.AddWithValue(name2, DBNull.Value);
            else cmd.Parameters.AddWithValue(name2, v);
        }
        if (rootId != 0) Add("$r", rootId);
        if (relPath is not null) Add("$p", relPath);
        if (absPath is not null) Add("$a", absPath);
        if (dirRel is not null) Add("$d", dirRel);
        if (name is not null) Add("$n", name);
        if (ext is not null) Add("$e", ext);
        if (size != 0) Add("$s", size);
        if (mtime != 0) Add("$m", mtime);
        Add("$c", ctime);
        Add("$t", takenAt);
        Add("$w", width);
        Add("$h", height);
        if (addedAt is not null) Add("$at", addedAt);
        if (updatedAt is not null) Add("$ut", updatedAt);
        if (id is not null) Add("$i", id);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public int MarkMissing(int rootId, HashSet<string> seenRel)
    {
        var gone = new List<int>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id,rel_path FROM images WHERE root_id=$r AND missing=0";
            cmd.Parameters.AddWithValue("$r", rootId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (!seenRel.Contains(r.GetString(1))) gone.Add(r.GetInt32(0));
        }
        if (gone.Count > 0)
        {
            foreach (var id in gone)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "UPDATE images SET missing=1 WHERE id=$i";
                cmd.Parameters.AddWithValue("$i", id);
                cmd.ExecuteNonQuery();
            }
        }
        return gone.Count;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public ImageRow? GetImage(int id) =>
        QueryOne("SELECT * FROM images WHERE id=$i", ("$i", id));

    [MethodImpl(MethodImplOptions.Synchronized)]
    public ImageRow? QueryOne(string sql, params (string, object)[] args)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadImage(r) : null;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<ImageRow> QueryAll(string sql, params (string, object)[] args)
    {
        var list = new List<ImageRow>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadImage(r));
        return list;
    }

    /// <summary>
    /// 按列名读取（容错缺失列/NULL）——自定义 SELECT（非 SELECT *）也安全。
    /// 此前按列序读，SidecarEntries/known 查询等自定义列序会崩（批次 2 引爆的批次 0 雷）。
    /// </summary>
    private ImageRow ReadImage(SqliteDataReader r)
    {
        int Ord(string c)
        {
            try { return r.GetOrdinal(c); } catch { return -1; }
        }
        T V<T>(string c, Func<int, T> f, T d)
        {
            var o = Ord(c);
            return o >= 0 && !r.IsDBNull(o) ? f(o) : d;
        }
        return new ImageRow(
            V("id", r.GetInt32, 0),
            V("root_id", r.GetInt32, 0),
            V("rel_path", r.GetString, ""),
            V("abs_path", r.GetString, ""),
            V("dir_rel", r.GetString, ""),
            V("name", r.GetString, ""),
            V("ext", r.GetString, ""),
            V("size", r.GetInt64, 0L),
            V("mtime", o => r.GetDouble(o), 0.0),
            V("ctime", o => (double?)r.GetDouble(o), (double?)null),
            V("taken_at", o => (double?)r.GetDouble(o), (double?)null),
            V("width", o => (int?)r.GetInt32(o), (int?)null),
            V("height", o => (int?)r.GetInt32(o), (int?)null),
            V("rating", r.GetInt32, 0),
            V("missing", o => r.GetInt64(o) != 0, false),
            V("added_at", o => r.GetDouble(o), 0.0),
            V("updated_at", o => r.GetDouble(o), 0.0),
            V("orig_md5", r.GetString, null),
            V("content_md5", r.GetString, null),
            V("page_url", r.GetString, null),
            V("source_url", r.GetString, null),
            V("nsfw", r.GetString, null),
            V("post_id", r.GetString, null),
            V("image_uuid", r.GetString, null),
            V("file_key", r.GetString, null),
            V("managed", o => r.GetInt64(o) != 0, false))
        { Tags = [] };
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public int CountImages(bool includeMissing = false)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = includeMissing
            ? "SELECT COUNT(*) FROM images"
            : "SELECT COUNT(*) FROM images WHERE missing=0";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public int CountFavorites()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM favorites";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public HashSet<int> FavoriteIds()
    {
        var set = new HashSet<int>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT image_id FROM favorites";
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetInt32(0));
        return set;
    }

    // ------------------------------------------------------------------ query

    public sealed record QuerySpec
    {
        public int? RootId { get; init; }
        public string? DirRel { get; init; }
        public bool Recursive { get; init; } = true;
        public bool FavoritesOnly { get; init; }
        public string Search { get; init; } = "";
        public string Sort { get; init; } = "taken";
        public bool Desc { get; init; }
        public int? Limit { get; init; }
        // ---- 阶段 C：分类筛选（照搬 store.py query_images 的标签参数） ----
        public IReadOnlyList<int> TagIds { get; init; } = [];
        public string TagMode { get; init; } = "or";   // or | and
        public IReadOnlyDictionary<string, IReadOnlyList<int>> TagGroups { get; init; } =
            new Dictionary<string, IReadOnlyList<int>>();
        public IReadOnlyDictionary<string, string> GroupModes { get; init; } =
            new Dictionary<string, string>();
        public IReadOnlyList<int> ExcludedTagIds { get; init; } = [];
        public bool Untagged { get; init; }
        /// <summary>「隐藏」时仅保留明确 SFW 标签，与旧版查询和保护策略一致。</summary>
        public bool HideNsfw { get; init; }
    }

    private static string LikeEscape(string s)
        => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// 对应 store.py query_images（批次 1 范围：目录/收藏/搜索/8 键排序）。
    /// 排序 SQL 逐字照搬 _SORT_SQL + name 特例（字母组 CASE 前置 + GALLERY_NATURAL）
    /// + 非 name 的稳定尾排序。
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<ImageRow> Query(QuerySpec spec)
    {
        var where = new List<string>
        {
            "i.missing = 0",
            "EXISTS(SELECT 1 FROM roots r WHERE r.id=i.root_id AND r.enabled=1)",
        };
        var args = new List<object>();

        if (spec.RootId is not null)
        {
            where.Add($"i.root_id = $p{args.Count}");
            args.Add(spec.RootId.Value);
        }
        if (spec.DirRel is not null)
        {
            if (spec.Recursive)
            {
                if (spec.DirRel.Length > 0)
                {
                    var i0 = args.Count;
                    args.Add(spec.DirRel);
                    args.Add(LikeEscape(spec.DirRel) + "/%");
                    where.Add($"(i.dir_rel = $p{i0} OR i.dir_rel LIKE $p{i0 + 1} ESCAPE '\\')");
                }
            }
            else
            {
                where.Add($"i.dir_rel = $p{args.Count}");
                args.Add(spec.DirRel);
            }
        }
        if (spec.FavoritesOnly)
        {
            where.Add("EXISTS(SELECT 1 FROM favorites f WHERE f.image_id=i.id)");
        }

        // 标签 IN 列表：顺序占位符，参数按出现顺序追加
        string InList(IReadOnlyList<int> ids)
        {
            var parts = new List<string>(ids.Count);
            foreach (var id in ids)
            {
                parts.Add($"$p{args.Count}");
                args.Add(id);
            }
            return string.Join(",", parts);
        }

        if (spec.HideNsfw)
        {
            // Gallery_Old：完全隐藏仅保留明确 SFW；与模糊及拖出保护共用标签口径。
            where.Add("EXISTS(SELECT 1 FROM image_tags safe_it JOIN tags safe_t ON safe_t.id=safe_it.tag_id " +
                      "WHERE safe_it.image_id=i.id AND safe_t.category='rating' AND safe_t.name='SFW')");
        }

        if (spec.Untagged)
        {
            where.Add("NOT EXISTS(SELECT 1 FROM image_tags it WHERE it.image_id=i.id)");
        }
        else if (spec.TagIds.Count > 0)
        {
            var tagIds = spec.TagIds.Distinct().ToList();
            var marks = InList(tagIds);
            if (spec.TagMode == "and")
            {
                var ph = $"$p{args.Count}";
                args.Add(tagIds.Count);
                where.Add("(SELECT COUNT(DISTINCT it.tag_id) FROM image_tags it " +
                          $"WHERE it.image_id=i.id AND it.tag_id IN ({marks})) = {ph}");
            }
            else
            {
                where.Add("EXISTS(SELECT 1 FROM image_tags it " +
                          $"WHERE it.image_id=i.id AND it.tag_id IN ({marks}))");
            }
        }
        foreach (var (category, group) in spec.TagGroups)
        {
            var ids = group.Distinct().ToList();
            if (ids.Count == 0) continue;
            var marks = InList(ids);
            if (spec.GroupModes.TryGetValue(category, out var mode) && mode == "all")
            {
                var ph = $"$p{args.Count}";
                args.Add(ids.Count);
                where.Add("(SELECT COUNT(*) FROM image_tags it " +
                          $"WHERE it.image_id=i.id AND it.tag_id IN ({marks}))={ph}");
            }
            else
            {
                where.Add("EXISTS(SELECT 1 FROM image_tags it " +
                          $"WHERE it.image_id=i.id AND it.tag_id IN ({marks}))");
            }
        }
        if (spec.ExcludedTagIds.Count > 0)
        {
            var marks = InList(spec.ExcludedTagIds.Distinct().ToList());
            where.Add("NOT EXISTS(SELECT 1 FROM image_tags it " +
                      $"WHERE it.image_id=i.id AND it.tag_id IN ({marks}))");
        }

        var search = spec.Search.Trim();
        if (search.Length > 0)
        {
            // galleryold 语义：标签 / 中文别名 / 文件名（别名经 tag_aliases 命中标签）
            var ph = $"$p{args.Count}";
            args.Add("%" + LikeEscape(search) + "%");
            where.Add(
                $"(i.name LIKE {ph} ESCAPE '\\' OR EXISTS(" +
                "SELECT 1 FROM image_tags it JOIN tags t ON t.id=it.tag_id " +
                "LEFT JOIN tag_profiles tp ON tp.tag_id=t.id " +
                $"WHERE it.image_id=i.id AND (t.name LIKE {ph} ESCAPE '\\' " +
                $"OR tp.zh LIKE {ph} ESCAPE '\\' OR tp.en LIKE {ph} ESCAPE '\\' " +
                $"OR EXISTS(SELECT 1 FROM tag_aliases ta WHERE ta.tag_id=t.id AND ta.alias LIKE {ph} ESCAPE '\\'))))");
        }

        string order;
        var sort = SortKeys.IsValid(spec.Sort) ? spec.Sort : "name";
        var direction = spec.Desc ? "DESC" : "ASC";
        if (sort == "name")
        {
            // 字母组永远在前（A–Z 归 0，其余归 1），组内按自然排序 + 方向
            order = "CASE WHEN substr(i.name,1,1) GLOB '[A-Za-z]' THEN 0 ELSE 1 END ASC, " +
                    $"i.name COLLATE GALLERY_NATURAL {direction}";
        }
        else
        {
            var col = sort switch
            {
                "modified" => "i.mtime",
                "taken" => "COALESCE(i.taken_at, i.mtime)",
                "created" => "COALESCE(i.ctime, i.mtime)",
                "added" => "i.added_at",
                "size" => "i.size",
                "rating" => "i.rating",
                "random" => "RANDOM()",
                _ => "i.name COLLATE NOCASE",
            };
            order = $"{col} {direction}" + (sort != "random" ? ", i.name COLLATE NOCASE ASC" : "");
        }

        var sql = "SELECT i.* FROM images i WHERE " + string.Join(" AND ", where) +
                  $" ORDER BY {order}";
        if (spec.Limit is not null) sql += $" LIMIT {spec.Limit.Value}";

        var rows = QueryAllWithAlias(sql, [.. args]);
        AttachTags(rows);
        return rows;
    }

    /// <summary>QueryAll 的可传参变体（参数带 $ 前缀按序填充）。</summary>
    private List<ImageRow> QueryAllWithAlias(string sql, List<object> args)
    {
        var list = new List<ImageRow>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < args.Count; i++)
            cmd.Parameters.AddWithValue($"$p{i}", args[i]);
        // SQL 里的占位符按出现顺序对应 $p0..$pn
        // （上面的 where 片段已改为使用 $p 占位符）
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadImage(r));
        return list;
    }

    private void AttachTags(List<ImageRow> rows)
    {
        if (rows.Count == 0) return;
        var map = new Dictionary<int, List<(string, string)>>();
        const int chunk = 900;
        var ids = rows.Select(r => r.Id).ToList();
        for (var i = 0; i < ids.Count; i += chunk)
        {
            var part = ids.Skip(i).Take(chunk).ToList();
            var marks = string.Join(",", part.Select((_, j) => $"$c{j}"));
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "SELECT it.image_id, COALESCE(t.category,'custom'), t.name " +
                "FROM image_tags it JOIN tags t ON t.id=it.tag_id " +
                $"WHERE it.image_id IN ({marks}) ORDER BY t.category, t.name";
            for (var j = 0; j < part.Count; j++)
                cmd.Parameters.AddWithValue($"$c{j}", part[j]);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt32(0);
                if (!map.TryGetValue(id, out var list))
                    map[id] = list = [];
                list.Add((r.GetString(1), r.GetString(2)));
            }
        }
        foreach (var row in rows)
            row.Tags = map.TryGetValue(row.Id, out var list)
                ? list
                : new List<(string, string)>();
    }

    /// <summary>
    /// 照搬 store.py root_containing：找出包含该路径的启用根（normcase 比较、
    /// 最长根优先），rel 保留原始大小写（与库内记录一致）。
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public (int RootId, string Rel)? RootContaining(string path)
    {
        var original = Path.GetFullPath(path);
        var target = FileIdentity.PathKey(original);
        (int Id, string Rel, int Depth)? best = null;
        foreach (var root in Roots())
        {
            if (!root.Enabled) continue;
            var rootAbs = Path.GetFullPath(root.Path);
            var rootKey = FileIdentity.PathKey(rootAbs);
            string rel;
            if (target == rootKey) rel = "";
            else if (IsUnder(target, rootKey))
                rel = Path.GetRelativePath(rootAbs, original).Replace('\\', '/');
            else continue;
            int depth = rootKey.Length;
            if (best is null || depth > best.Value.Depth)
                best = (root.Id, rel, depth);
        }
        return best is null ? null : (best.Value.Id, best.Value.Rel);
    }

    // ------------------------------------------------------------------ query

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<FolderCount> Folders(int rootId)
    {
        var list = new List<FolderCount>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT dir_rel, COUNT(*) AS c FROM images i " +
            "WHERE i.root_id=$r AND i.missing=0 GROUP BY dir_rel ORDER BY dir_rel COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$r", rootId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new FolderCount(r.GetString(0), r.GetInt32(1)));
        return list;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public (int RootId, string DirRel)? FolderOf(int imageId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT root_id,dir_rel FROM images WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", imageId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return (r.GetInt32(0), r.GetString(1));
    }

    // ------------------------------------------------------------------- tags

    [MethodImpl(MethodImplOptions.Synchronized)]
    public int EnsureTag(string name, string category = "custom")
    {
        var clean = (name ?? "").Trim();
        int found;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT id FROM tags WHERE name=$n COLLATE NOCASE AND COALESCE(category,'custom')=$c";
            cmd.Parameters.AddWithValue("$n", clean);
            cmd.Parameters.AddWithValue("$c", category);
            found = Convert.ToInt32(cmd.ExecuteScalar() ?? -1);
        }
        if (found > 0) return found;

        // 合并过的旧名字：先按「同分类别名」重定向到留下的标签，
        // 否则同一位画师/同一作品的旧写法（重新下载、重导 sidecar）会再分裂出一个新标签。
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT t.id FROM tag_aliases a JOIN tags t ON t.id=a.tag_id " +
                "WHERE a.alias=$n AND (COALESCE(t.category,'custom')=$c " +
                "OR ($c='custom' AND COALESCE(t.category,'custom')='character')) LIMIT 1";
            cmd.Parameters.AddWithValue("$n", clean);
            cmd.Parameters.AddWithValue("$c", category);
            var redirected = Convert.ToInt32(cmd.ExecuteScalar() ?? -1);
            if (redirected > 0) return redirected;
        }

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO tags(name,category) VALUES($n,$c); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$n", clean);
            cmd.Parameters.AddWithValue("$c", category);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public int AddTags(IReadOnlyCollection<int> imageIds, IEnumerable<string> names)
    {
        var cleaned = names.Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
        if (imageIds.Count == 0 || cleaned.Count == 0) return 0;
        var tagIds = cleaned.Select(n => EnsureTag(n)).ToList();
        foreach (var i in imageIds)
            foreach (var t in tagIds)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "INSERT INTO image_tags(image_id,tag_id) VALUES($i,$t) ON CONFLICT DO NOTHING";
                cmd.Parameters.AddWithValue("$i", i);
                cmd.Parameters.AddWithValue("$t", t);
                cmd.ExecuteNonQuery();
            }
        Commit();
        SyncSidecars(imageIds);
        return imageIds.Count;
    }

    /// <summary>整体替换标签集（managed=1），对应 set_categorized_tags。</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetCategorizedTags(IReadOnlyCollection<int> imageIds,
        IReadOnlyCollection<(string Category, string Name)> pairs)
    {
        if (imageIds.Count == 0) return;
        var tagIds = new List<int>();
        var seen = new HashSet<int>();
        foreach (var (category, name0) in pairs)
        {
            var name = (name0 ?? "").Trim();
            if (name.Length == 0) continue;
            var tid = EnsureTag(name, category ?? "custom");
            if (seen.Add(tid)) tagIds.Add(tid);
        }
        var idList = imageIds.ToList();
        var idMarks = string.Join(",", idList.Select((_, i) => $"$id{i}"));
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = $"DELETE FROM image_tags WHERE image_id IN ({idMarks})";
            for (var i = 0; i < idList.Count; i++)
                cmd.Parameters.AddWithValue($"$id{i}", idList[i]);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO image_tags(image_id,tag_id) VALUES($i,$t) ON CONFLICT DO NOTHING";
            var pi = cmd.Parameters.Add("$i", SqliteType.Integer);
            var pt = cmd.Parameters.Add("$t", SqliteType.Integer);
            foreach (var i in idList)
                foreach (var t in tagIds)
                {
                    pi.Value = i; pt.Value = t;
                    cmd.ExecuteNonQuery();
                }
        }
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = $"UPDATE images SET managed=1 WHERE id IN ({idMarks})";
            for (var i = 0; i < idList.Count; i++)
                cmd.Parameters.AddWithValue($"$id{i}", idList[i]);
            cmd.ExecuteNonQuery();
        }
        Commit();
        if (_inTransaction == 0) SyncSidecars(imageIds);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<(string Category, string Name)> CategorizedTags(int imageId)
    {
        var list = new List<(string, string)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT COALESCE(t.category,'custom'),t.name FROM tags t " +
            "JOIN image_tags it ON it.tag_id=t.id WHERE it.image_id=$i ORDER BY t.category,t.name";
        cmd.Parameters.AddWithValue("$i", imageId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.IsDBNull(0) ? "custom" : r.GetString(0), r.GetString(1)));
        return list;
    }

    /// <summary>待判定的图片：返回 (image_id, 绝对路径)。onlyStale 时跳过已经用同一模型跑过的。</summary>
    public List<KeyValuePair<int, string>> ImageFilesForScan(string modelHash, bool onlyStale)
    {
        lock (SyncRoot)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = onlyStale
                ? "SELECT i.id, r.path, i.rel_path FROM images i JOIN roots r ON r.id=i.root_id " +
                  "LEFT JOIN nsfw_scores s ON s.image_id=i.id " +
                  "WHERE i.missing=0 AND (s.image_id IS NULL OR s.model_hash<>$h) ORDER BY i.id"
                : "SELECT i.id, r.path, i.rel_path FROM images i JOIN roots r ON r.id=i.root_id " +
                  "WHERE i.missing=0 ORDER BY i.id";
            if (onlyStale) cmd.Parameters.AddWithValue("$h", modelHash);

            var list = new List<KeyValuePair<int, string>>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var root = r.GetString(1);
                var rel = r.GetString(2);
                list.Add(new KeyValuePair<int, string>(
                    r.GetInt32(0), Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))));
            }
            return list;
        }
    }

    // ---------------------------------------------------------------- 敏感判定

    /// <summary>批量取现有分级（images.nsfw），给「重判定预览」对比新旧用。</summary>
    public Dictionary<int, string> NsfwRatings(IEnumerable<int> imageIds)
    {
        var map = new Dictionary<int, string>();
        var ids = imageIds.ToList();
        lock (SyncRoot)
        {
            for (var offset = 0; offset < ids.Count; offset += 900)
            {
                var chunk = ids.Skip(offset).Take(900).ToList();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText =
                    "SELECT id, nsfw FROM images WHERE id IN (" +
                    string.Join(",", chunk.Select((_, i) => "$p" + i)) + ")";
                for (var i = 0; i < chunk.Count; i++) cmd.Parameters.AddWithValue("$p" + i, chunk[i]);

                using var r = cmd.ExecuteReader();
                while (r.Read()) map[r.GetInt32(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
            }
        }
        return map;
    }

    public sealed record NsfwScore(
        int ImageId, string Source, string Rating, string Value, double NsfwProb,
        double Confidence, double Margin, string ModelName, string ModelHash,
        string Detail, double CheckedAt)
    {
        /// <summary>danbooru 四级里 g/s 算 SFW，q/e 算 NSFW。</summary>
        public bool IsNsfw => Rating is "q" or "e";
    }

    /// <summary>写一条判定：nsfw_scores 落档 + images.nsfw 同步；tagIt 时顺手打 NSFW/SFW 标签。</summary>
    public void SetNsfwScore(int imageId, string source, string rating, string value = "",
        double nsfwProb = 0, double confidence = 0, double margin = 0,
        string modelName = "", string modelHash = "", string detail = "", bool tagIt = false)
    {
        lock (SyncRoot)
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO nsfw_scores(image_id,source,rating,value,nsfw_prob,confidence,margin," +
                    "model_name,model_hash,detail,checked_at) VALUES($i,$s,$r,$v,$p,$c,$m,$n,$h,$d,$t) " +
                    "ON CONFLICT(image_id) DO UPDATE SET source=$s,rating=$r,value=$v,nsfw_prob=$p," +
                    "confidence=$c,margin=$m,model_name=$n,model_hash=$h,detail=$d,checked_at=$t";
                cmd.Parameters.AddWithValue("$i", imageId);
                cmd.Parameters.AddWithValue("$s", source);
                cmd.Parameters.AddWithValue("$r", rating);
                cmd.Parameters.AddWithValue("$v", value);
                cmd.Parameters.AddWithValue("$p", nsfwProb);
                cmd.Parameters.AddWithValue("$c", confidence);
                cmd.Parameters.AddWithValue("$m", margin);
                cmd.Parameters.AddWithValue("$n", modelName);
                cmd.Parameters.AddWithValue("$h", modelHash);
                cmd.Parameters.AddWithValue("$d", detail);
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE images SET nsfw=$r WHERE id=$i";
                cmd.Parameters.AddWithValue("$r", rating);
                cmd.Parameters.AddWithValue("$i", imageId);
                cmd.ExecuteNonQuery();
            }
        }

        if (tagIt) TagNsfw(imageId, rating is "q" or "e");
    }

    /// <summary>清掉一张图的判定：删 nsfw_scores 那一条 + images.nsfw 置空（用户主动删掉分级芯片后用）。</summary>
    public void ClearNsfwScore(int imageId)
    {
        lock (SyncRoot)
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM nsfw_scores WHERE image_id=$i";
                cmd.Parameters.AddWithValue("$i", imageId);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE images SET nsfw=NULL WHERE id=$i";
                cmd.Parameters.AddWithValue("$i", imageId);
                cmd.ExecuteNonQuery();
            }
        }
    }

    /// <summary>NSFW/SFW 标签互斥写入。</summary>
    /// <summary>
    /// 修历史漂移：① images.nsfw 和 nsfw_scores.rating 不一致时以评分表为准；
    /// ② 分辨率标签被误删的图（老版本「整批替换」的受害者）补回来。
    /// 只补不改别的，重复跑是幂等的。
    /// </summary>
    public (int NsfwFixed, int ResolutionFixed) RepairDerived()
    {
        lock (SyncRoot)
        {
            int nsfwFixed;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText =
                    "UPDATE images SET nsfw=(SELECT s.rating FROM nsfw_scores s WHERE s.image_id=images.id) " +
                    "WHERE EXISTS(SELECT 1 FROM nsfw_scores s WHERE s.image_id=images.id) " +
                    "AND COALESCE(nsfw,'')<>COALESCE((SELECT s.rating FROM nsfw_scores s WHERE s.image_id=images.id),'')";
                nsfwFixed = cmd.ExecuteNonQuery();
            }

            var rows = new List<(int Id, int? W, int? H)>();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT i.id,i.width,i.height FROM images i " +
                    "WHERE i.missing=0 AND i.width>0 AND i.height>0 AND NOT EXISTS(" +
                    "SELECT 1 FROM image_tags it JOIN tags t ON t.id=it.tag_id " +
                    "WHERE it.image_id=i.id AND t.category='resolution')";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    rows.Add((r.GetInt32(0), r.IsDBNull(1) ? null : r.GetInt32(1),
                        r.IsDBNull(2) ? null : r.GetInt32(2)));
            }

            foreach (var (id, w, h) in rows)
            {
                var pairs = CategorizedTags(id).Where(p => p.Category != "resolution").ToList();
                pairs.AddRange(Taxonomy.ResolutionTags(w, h).Select(t => ("resolution", t)));
                SetCategorizedTags([id], pairs);
            }

            Commit();
            return (nsfwFixed, rows.Count);
        }
    }

    public void TagNsfw(int imageId, bool nsfw)
    {
        var keep = nsfw ? "NSFW" : "SFW";
        var drop = nsfw ? "SFW" : "NSFW";

        // SetCategorizedTags 是「整张图整批替换」，所以要把除 rating 之外的标签原样带上，
        // 否则标个分级会把画师/作品/角色全冲掉。
        var pairs = CategorizedTags(imageId)
            .Where(t => t.Category != "rating" && t.Name != keep && t.Name != drop)
            .Select(t => (t.Category, t.Name))
            .ToList();
        pairs.Add(("rating", keep));
        SetCategorizedTags([imageId], pairs);
    }

    public NsfwScore? NsfwScoreFor(int imageId)
    {
        lock (SyncRoot)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "SELECT image_id,source,rating,value,nsfw_prob,confidence,margin,model_name," +
                "model_hash,detail,checked_at FROM nsfw_scores WHERE image_id=$i";
            cmd.Parameters.AddWithValue("$i", imageId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new NsfwScore(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetString(7), r.GetString(8),
                r.GetString(9), r.GetDouble(10));
        }
    }

    /// <summary>已分级 / 已判敏感 / 仍未知 三个计数（设置页与状态行共用）。</summary>
    public (int Scored, int Nsfw, int Unknown) NsfwSummary()
    {
        lock (SyncRoot)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FILTER (WHERE nsfw IS NOT NULL AND nsfw<>'')," +
                "COUNT(*) FILTER (WHERE nsfw IN ('q','e'))," +
                "COUNT(*) FILTER (WHERE nsfw IS NULL OR nsfw='') FROM images";
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)) : (0, 0, 0);
        }
    }

    // ---------------------------------------------------------------- 浏览器桥接
    /// <summary>桥接写入结果；Duplicate 表示 ingest_id 已经收过（幂等）。</summary>
    public sealed record IngestResult(int QueueId, bool Duplicate);

    private bool _bridgeSchemaReady;

    /// <summary>把下载记录中的网页和原始出处补到图库；只填空值，保留用户已有来源。</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool ApplyIngestOrigin(int imageId, string payload)
    {
        string? pageUrl, sourceUrl;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            string? Read(string key) => document.RootElement.TryGetProperty(key, out var value) &&
                value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                    ? value.GetString()!.Trim() : null;
            pageUrl = Read("page_url");
            sourceUrl = Read("source");
        }
        catch (JsonException) { return false; }
        if (pageUrl is null && sourceUrl is null) return false;
        using var command = _conn.CreateCommand();
        command.CommandText =
            "UPDATE images SET page_url=COALESCE(NULLIF(TRIM(page_url),''),$page)," +
            "source_url=COALESCE(NULLIF(TRIM(source_url),''),$source) WHERE id=$id AND " +
            "((NULLIF(TRIM(page_url),'') IS NULL AND $page IS NOT NULL) OR " +
            "(NULLIF(TRIM(source_url),'') IS NULL AND $source IS NOT NULL))";
        command.Parameters.AddWithValue("$id", imageId);
        command.Parameters.AddWithValue("$page", (object?)pageUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", (object?)sourceUrl ?? DBNull.Value);
        if (command.ExecuteNonQuery() == 0) return false;
        SyncSidecars([imageId]);
        return true;
    }

    /// <summary>从成功归档作业补回历史缺失来源；只匹配实际归档路径，不按文件名猜测。</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public int RestoreArchivedOrigins()
    {
        var records = new List<(int Id, string Payload)>();
        using (var command = _conn.CreateCommand())
        {
            command.CommandText =
                "SELECT i.id,j.payload FROM images i JOIN archive_jobs j ON " +
                "i.abs_path=j.destination COLLATE NOCASE WHERE j.phase='moved' AND " +
                "(NULLIF(TRIM(i.page_url),'') IS NULL OR NULLIF(TRIM(i.source_url),'') IS NULL) " +
                "ORDER BY j.updated_at";
            using var reader = command.ExecuteReader();
            while (reader.Read()) records.Add((reader.GetInt32(0), reader.GetString(1)));
        }
        return records.Count(row => ApplyIngestOrigin(row.Id, row.Payload));
    }

    /// <summary>桥接运行期建表（不改旧库既有结构，只加自己的幂等表）。</summary>
    public void EnsureBridgeSchema()
    {
        lock (SyncRoot)
        {
            if (_bridgeSchemaReady) return;
            var conn = _conn;
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE IF NOT EXISTS bridge_ingest(" +
                "ingest_id TEXT PRIMARY KEY, queue_id INTEGER NOT NULL, received_at REAL NOT NULL)";
            cmd.ExecuteNonQuery();
            _bridgeSchemaReady = true;
        }
    }

    /// <summary>
    /// 把扩展送来的一条下载记录写进 ingest_queue（同步落库后才返回，插件据此决定重试）。
    /// ingest_id 非空时按它做幂等。
    /// </summary>
    public IngestResult IngestPush(string payloadJson, string inboxName, string inboxPath,
        string pageUrl, string imageUrl, string postId, string md5, string rating, string source,
        string ingestId)
    {
        EnsureBridgeSchema();

        lock (SyncRoot)
        {
            var conn = _conn;
            using var tx = conn.BeginTransaction();

            if (ingestId.Length > 0)
            {
                using var dup = conn.CreateCommand();
                dup.Transaction = tx;
                dup.CommandText = "SELECT queue_id FROM bridge_ingest WHERE ingest_id=$i";
                dup.Parameters.AddWithValue("$i", ingestId);
                var found = dup.ExecuteScalar();
                if (found is not null and not DBNull)
                    return new IngestResult(Convert.ToInt32(found), true);
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO ingest_queue(received_at,state,inbox_path,inbox_name,page_url,image_url," +
                "post_id,md5,rating,source,payload,note) VALUES($t,'pending',$p,$n,$u,$i,$pid,$md5,$r,$s,$j,'')";
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
            cmd.Parameters.AddWithValue("$p", inboxPath);
            cmd.Parameters.AddWithValue("$n", inboxName);
            cmd.Parameters.AddWithValue("$u", pageUrl);
            cmd.Parameters.AddWithValue("$i", imageUrl);
            cmd.Parameters.AddWithValue("$pid", postId);
            cmd.Parameters.AddWithValue("$md5", md5);
            cmd.Parameters.AddWithValue("$r", rating);
            cmd.Parameters.AddWithValue("$s", source);
            cmd.Parameters.AddWithValue("$j", payloadJson);
            cmd.ExecuteNonQuery();

            using var idCmd = conn.CreateCommand();
            idCmd.Transaction = tx;
            idCmd.CommandText = "SELECT last_insert_rowid()";
            var id = Convert.ToInt32(idCmd.ExecuteScalar() ?? 0);

            if (ingestId.Length > 0)
            {
                using var seen = conn.CreateCommand();
                seen.Transaction = tx;
                seen.CommandText =
                    "INSERT OR REPLACE INTO bridge_ingest(ingest_id,queue_id,received_at) VALUES($i,$q,$t)";
                seen.Parameters.AddWithValue("$i", ingestId);
                seen.Parameters.AddWithValue("$q", id);
                seen.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
                seen.ExecuteNonQuery();
            }

            tx.Commit();
            return new IngestResult(id, false);
        }
    }

    /// <summary>本地导入：把文件路径写进待归档队列，跳过队列里未忽略的或图库里已有的同路径。</summary>
    public (int Added, int Skipped) EnqueueLocalImages(IReadOnlyList<string> paths, bool keepSource)
    {
        EnsureBridgeSchema();
        lock (SyncRoot)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COALESCE(inbox_path,'') FROM ingest_queue WHERE state!='ignored'";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var path = r.GetString(0);
                    if (path.Length > 0) known.Add(path);
                }
            }

            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT abs_path FROM images";
                using var r = cmd.ExecuteReader();
                while (r.Read()) known.Add(r.GetString(0));
            }

            var added = 0;
            var skipped = 0;
            using var tx = _conn.BeginTransaction();
            using var insert = _conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText =
                "INSERT INTO ingest_queue(received_at,state,inbox_path,inbox_name,page_url,image_url," +
                "post_id,md5,rating,source,payload,note) VALUES($t,'pending',$p,$n,'',''," +
                "'','','','',$j,'')";
            insert.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
            insert.Parameters.AddWithValue("$p", "");
            insert.Parameters.AddWithValue("$n", "");
            insert.Parameters.AddWithValue("$j", "");

            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string full;
                try { full = Path.GetFullPath(path); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    skipped++;
                    continue;
                }

                if (!File.Exists(full) || !known.Add(full))
                {
                    skipped++;
                    continue;
                }

                insert.Parameters["$p"].Value = full;
                insert.Parameters["$n"].Value = Path.GetFileName(full);
                insert.Parameters["$j"].Value = JsonSerializer.Serialize(new
                {
                    filename = full,
                    imported = true,
                    import_keep_source = keepSource,
                });
                insert.ExecuteNonQuery();
                added++;
            }

            tx.Commit();
            return (added, skipped);
        }
    }

    /// <summary>待处理（pending）条数，供 /ping 与侧栏指示使用。</summary>
    public int PendingIngestCount()
    {
        lock (SyncRoot)
        {
            var conn = _conn;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM ingest_queue WHERE state='pending'";
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
    }

    /// <summary>审核面板用的一条待处理记录（payload 原文保留给详情显示）。</summary>
    public sealed record PendingIngestRow(
        int Id, double ReceivedAt, string State, string InboxPath, string InboxName,
        string PageUrl, string ImageUrl, string PostId, string Md5, string Rating,
        string Source, string Payload, string Note)
    {
        /// <summary>历史记录解析出的当前文件位置；null = 尚未解析（待归档/丢失不解析）。</summary>
        public string? ResolvedPath { get; init; }
    }

    private const string IngestColumns =
        "id,received_at,state,COALESCE(inbox_path,''),COALESCE(inbox_name,'')," +
        "COALESCE(page_url,''),COALESCE(image_url,''),COALESCE(post_id,''),COALESCE(md5,'')," +
        "COALESCE(rating,''),COALESCE(source,''),payload,COALESCE(note,'')";

    private static PendingIngestRow ReadIngest(SqliteDataReader r) => new(
        r.GetInt32(0), r.GetDouble(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9),
        r.GetString(10), r.GetString(11), r.GetString(12));

    /// <summary>按状态取记录；state 为空表示只看 pending。</summary>
    public List<PendingIngestRow> IngestRows(string state = "pending", int limit = 500)
    {
        lock (SyncRoot)
        {
            using var cmd = _conn.CreateCommand();
            var where = state.Length > 0 ? "WHERE state=$s " : "";
            cmd.CommandText = "SELECT " + IngestColumns + " FROM ingest_queue " + where +
                "ORDER BY received_at DESC LIMIT $n";
            if (state.Length > 0) cmd.Parameters.AddWithValue("$s", state);
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            var list = new List<PendingIngestRow>();
            while (r.Read()) list.Add(ReadIngest(r));
            return list;
        }
    }

    /// <summary>历史视图：已归档 + 已忽略，按接收时间倒序。</summary>
    public List<PendingIngestRow> IngestHistory(int limit = 200)
    {
        lock (SyncRoot)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT " + IngestColumns + " FROM ingest_queue " +
                "WHERE state IN ('done','ignored') ORDER BY received_at DESC, id DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            var list = new List<PendingIngestRow>();
            while (r.Read()) list.Add(ReadIngest(r));
            return list;
        }
    }

    /// <summary>按 id 取一条记录（归档用）。</summary>
    public PendingIngestRow? IngestRow(int id)
    {
        lock (SyncRoot)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT " + IngestColumns + " FROM ingest_queue WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadIngest(r) : null;
        }
    }

    /// <summary>读某条归档作业（按目标路径），用于界面显示阶段与错误。</summary>
    public (string Phase, string Error)? ArchiveJob(string destination)
    {
        lock (SyncRoot)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "SELECT phase,error FROM archive_jobs WHERE destination=$d ORDER BY updated_at DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$d", destination);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return (r.GetString(0), r.GetString(1));
        }
    }

    /// <summary>写一条归档作业记录（对齐 archive_jobs 表）。</summary>
    public void SaveArchiveJob(string id, string source, string destination, string phase,
        string payload, string error)
    {
        lock (SyncRoot)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO archive_jobs(id,source,destination,phase,payload,error,updated_at) " +
                "VALUES($id,$s,$d,$p,$j,$e,$t) " +
                "ON CONFLICT(id) DO UPDATE SET phase=excluded.phase,error=excluded.error," +
                "updated_at=excluded.updated_at";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$s", source);
            cmd.Parameters.AddWithValue("$d", destination);
            cmd.Parameters.AddWithValue("$p", phase);
            cmd.Parameters.AddWithValue("$j", payload);
            cmd.Parameters.AddWithValue("$e", error);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>改记录状态（pending / done / ignored），note 存归档目标或忽略原因。</summary>
    public void SetIngestState(int id, string state, string note)
    {
        lock (SyncRoot)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE ingest_queue SET state=$s, note=$n WHERE id=$id";
            cmd.Parameters.AddWithValue("$s", state);
            cmd.Parameters.AddWithValue("$n", note);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>仅在记录仍处于预期状态时迁移，避免后台对账覆盖用户操作。</summary>
    public bool TrySetIngestState(int id, string expectedState, string state, string note)
    {
        lock (SyncRoot)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "UPDATE ingest_queue SET state=$s, note=$n WHERE id=$id AND state=$expected";
            cmd.Parameters.AddWithValue("$s", state);
            cmd.Parameters.AddWithValue("$n", note);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$expected", expectedState);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>保存待归档图片的人工标签；只更新尚未处理的记录，不改扩展的幂等身份。</summary>
    public void UpdateIngestReview(int id, string payload, string rating)
    {
        lock (SyncRoot)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE ingest_queue SET payload=$p,rating=$r WHERE id=$id AND state='pending'";
            cmd.Parameters.AddWithValue("$p", payload);
            cmd.Parameters.AddWithValue("$r", rating);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 归档成功：写最终文件路径，并把归档时的图片 id 记进 payload。
    /// 历史视图靠这个 id 找到图片当前所在位置，而不是依赖会过期的归档路径。
    /// </summary>
    public void SetIngestArchived(int id, string destination, string payload)
    {
        lock (SyncRoot)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE ingest_queue SET state='done',note=$n,payload=$p WHERE id=$id";
            cmd.Parameters.AddWithValue("$n", destination);
            cmd.Parameters.AddWithValue("$p", payload);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>彻底删掉一条记录（不动磁盘文件）。</summary>
    public void DeleteIngest(int id)
    {
        lock (SyncRoot)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM ingest_queue WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public readonly record struct IngestHistoryCleanup(int Deleted, int KeptExistingSource);

    /// <summary>
    /// 删除早于 cutoff 的已归档/已忽略记录，只动队列、不动文件。
    /// 默认跳过“原文件仍在”的已归档记录：删掉后同一路径能再次导入，会生成重复副本。
    /// </summary>
    public IngestHistoryCleanup DeleteIngestHistory(double cutoffUnix, bool includeExistingSources)
    {
        lock (SyncRoot)
        {
            var candidates = new List<(int Id, string State, string InboxPath)>();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id,state,COALESCE(inbox_path,'') FROM ingest_queue " +
                                  "WHERE state IN ('done','ignored') AND received_at < $c";
                cmd.Parameters.AddWithValue("$c", cutoffUnix);
                using var r = cmd.ExecuteReader();
                while (r.Read()) candidates.Add((r.GetInt32(0), r.GetString(1), r.GetString(2)));
            }

            var deleted = 0;
            var kept = 0;
            using var tx = Transaction();
            using var del = _conn.CreateCommand();
            del.CommandText = "DELETE FROM ingest_queue WHERE id=$id";
            del.Parameters.AddWithValue("$id", 0);
            foreach (var (id, state, inbox) in candidates)
            {
                if (!includeExistingSources && state == "done" && inbox.Length > 0 && File.Exists(inbox))
                {
                    kept++;
                    continue;
                }
                del.Parameters["$id"].Value = id;
                deleted += del.ExecuteNonQuery();
            }
            tx.Success = true;
            return new IngestHistoryCleanup(deleted, kept);
        }
    }

    /// <summary>各状态条数（侧栏角标用）。</summary>
    public Dictionary<string, int> IngestStats()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        lock (SyncRoot)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT state, COUNT(*) FROM ingest_queue GROUP BY state";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = r.IsDBNull(0) ? "" : r.GetString(0);
                map[key] = r.GetInt32(1);
            }
        }
        return map;
    }

    /// <summary>一条可恢复的同步/归档失败记录（path 去重，重复失败只更新消息与时间）。</summary>
    public sealed record SyncError(string Path, string Message, double UpdatedAt);

    /// <summary>记一条失败。归档/桥接出错时调用，设置页「最近错误」读这里。</summary>
    public void RecordSyncError(string path, string message)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (SyncRoot)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sync_errors(path, message, updated_at) VALUES($p, $m, $t)
                ON CONFLICT(path) DO UPDATE SET message = excluded.message, updated_at = excluded.updated_at
                """;
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$m", message);
            cmd.Parameters.AddWithValue("$t", (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0));
            cmd.ExecuteNonQuery();
        }
    }

    public List<SyncError> SyncErrors(int limit = 50)
    {
        var list = new List<SyncError>();
        lock (SyncRoot)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT path, message, updated_at FROM sync_errors ORDER BY updated_at DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new SyncError(r.GetString(0), r.GetString(1), r.GetDouble(2)));
        }
        return list;
    }

    public void ClearSyncError(string path)
    {
        lock (SyncRoot)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM sync_errors WHERE path = $p";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.ExecuteNonQuery();
        }
    }

    public int SyncErrorCount()
    {
        lock (SyncRoot)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sync_errors";
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
    }

    public void ClearSyncErrors()
    {
        lock (SyncRoot)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM sync_errors";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>最近收到的 N 条（调试与审核面板用）。</summary>
    public List<(int Id, string Name, string State, double ReceivedAt)> RecentIngest(int limit = 20)
    {
        var list = new List<(int, string, string, double)>();
        lock (SyncRoot)
        {
            var conn = _conn;
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT id,COALESCE(inbox_name,''),state,received_at FROM ingest_queue " +
                "ORDER BY received_at DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetDouble(3)));
        }
        return list;
    }

    /// <summary>标签视图（词库补齐的中文/英文名 + 别名 + 使用数），对应 store.py TagInfo。</summary>
    public sealed record TagInfo
    {
        public required int Id { get; init; }
        public required string Name { get; init; }
        public string? Color { get; init; }
        public int Count { get; init; }
        public string Category { get; init; } = "custom";
        public IReadOnlyList<string> Aliases { get; init; } = [];
        public string ZhName { get; init; } = "";
        public string EnName { get; init; } = "";
        public string EntityKey { get; init; } = "";
        /// <summary>标签所绑定的词库实体所属作品；用于搜索时避免把同作品的同一来源名再加一遍。</summary>
        public string GameKey { get; init; } = "";
        /// <summary>人工状态：manual / resolved / external / original；空 = 未处理。</summary>
        public string Status { get; init; } = "";
        /// <summary>同一身份的身份键（跨作品/跨写法共用）。</summary>
        public string IdentityKey { get; init; } = "";
        /// <summary>起源作品（派生/联动时与标签所在作品不同）。</summary>
        public string OriginGame { get; init; } = "";
        /// <summary>显示模式：zh_full / zh_short / alias / original / canonical。</summary>
        public string DisplayMode { get; init; } = "";
        /// <summary>人工指定的显示名；非空时优先于显示模式。</summary>
        public string DisplayName { get; init; } = "";
        /// <summary>贴片实际显示名：人工显示名 > 显示模式结果 > 中文名 > 英文来源名 > 标签名。</summary>
        public string DisplayOverride { get; init; } = "";
        public string Display => DisplayOverride.Length > 0
            ? DisplayOverride
            : ZhName.Length > 0 ? ZhName : EnName.Length > 0 ? EnName : Name;

        /// <summary>搜索用的全部可命中写法：标签名 + 别名 + 词库补出的中/英文名。</summary>
        public IReadOnlyList<string> SearchTerms
        {
            get
            {
                var list = new List<string>(Aliases.Count + 4) { Name };
                list.AddRange(Aliases);
                if (ZhName.Length > 0) list.Add(ZhName);
                if (EnName.Length > 0) list.Add(EnName);
                if (DisplayName.Length > 0) list.Add(DisplayName);
                return list;
            }
        }
    }

    private static string FoldForMatch(string s)
        => s.Trim().ToLowerInvariant().Replace(' ', '_');

    /// <summary>照搬 store.py all_tags：全量标签 + 别名 + 词库补充（tag_profiles）。</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<TagInfo> AllTags()
    {
        var aliases = new Dictionary<int, List<string>>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT tag_id,alias FROM tag_aliases ORDER BY alias";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt32(0);
                if (!aliases.TryGetValue(id, out var list)) aliases[id] = list = [];
                list.Add(r.GetString(1));
            }
        }

        var states = TagDisplayState.Load(_conn);
        var globalMode = TagDisplayState.GlobalMode(_conn);
        var tags = new List<TagInfo>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT t.id,t.name,t.color,COALESCE(t.category,'custom')," +
                "(SELECT COUNT(*) FROM image_tags it WHERE it.tag_id=t.id) AS c," +
                "COALESCE(p.zh,''),COALESCE(p.en,''),COALESCE(p.entity_key,'')," +
                "COALESCE(e.identity_key,''),COALESCE(e.game_key,'') " +
                "FROM tags t LEFT JOIN tag_profiles p ON p.tag_id=t.id " +
                "LEFT JOIN catalog_entities e ON e.entity_key=p.entity_key " +
                "ORDER BY t.name COLLATE NOCASE";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt32(0);
                states.TryGetValue(id, out var state);
                var mode = state is not null && state.DisplayMode.Length > 0
                    ? state.DisplayMode
                    : globalMode;
                tags.Add(new TagInfo
                {
                    Id = id,
                    Name = r.GetString(1),
                    Color = r.IsDBNull(2) ? null : r.GetString(2),
                    Category = r.IsDBNull(3) ? "custom" : r.GetString(3),
                    Count = r.GetInt32(4),
                    ZhName = r.GetString(5),
                    EnName = r.GetString(6),
                    EntityKey = r.GetString(7),
                    GameKey = r.GetString(9),
                    Aliases = aliases.TryGetValue(id, out var al) ? al : [],
                    Status = state?.Status ?? "",
                    // 词库实体的身份是权威值；状态里的身份只用于没有 profile 的标签。
                    IdentityKey = r.GetString(8).Length > 0
                        ? r.GetString(8)
                        : state?.IdentityKey ?? "",
                    OriginGame = state?.OriginGame ?? "",
                    DisplayMode = mode,
                    DisplayName = state?.DisplayName ?? "",
                });
            }
        }

        // 中文短名要跨「同一身份」的所有写法取最短；只有真的用到时才扫实体表。
        Dictionary<string, string>? entityIdentity = null;
        Dictionary<string, string>? shortNames = null;
        if (tags.Any(t => t.DisplayMode == "zh_short"))
        {
            entityIdentity = new Dictionary<string, string>(StringComparer.Ordinal);
            shortNames = new Dictionary<string, string>(StringComparer.Ordinal);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "SELECT entity_key,COALESCE(NULLIF(identity_key,''),entity_key)," +
                "COALESCE(zh,''),COALESCE(aliases,'[]') FROM catalog_entities";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = r.GetString(0);
                var identity = r.GetString(1);
                entityIdentity[key] = identity;
                var candidates = new List<string>();
                var zh = r.GetString(2);
                if (zh.Length > 0) candidates.Add(zh);
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<string>>(r.GetString(3));
                    if (parsed is not null)
                        candidates.AddRange(parsed.Where(a => !string.IsNullOrWhiteSpace(a)));
                }
                catch
                {
                    // 别名 JSON 坏了就只用中文名。
                }
                foreach (var candidate in candidates)
                {
                    if (!shortNames.TryGetValue(identity, out var old) ||
                        candidate.Length < old.Length ||
                        (candidate.Length == old.Length &&
                         string.CompareOrdinal(candidate, old) < 0))
                        shortNames[identity] = candidate;
                }
            }
        }

        for (var i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];
            var display = tag.DisplayName;
            if (display.Length == 0)
            {
                // 默认：有中文用中文；没有中文用英文来源名；再没有才用标签原名。
                var zhFull = tag.ZhName.Length > 0
                    ? tag.ZhName
                    : tag.EnName.Length > 0 ? tag.EnName : tag.Name;
                display = tag.DisplayMode switch
                {
                    "original" => tag.EnName.Length > 0 ? tag.EnName : tag.Name,
                    "canonical" => tag.Name,
                    "alias" => tag.Aliases.Count > 0 ? tag.Aliases[0] : zhFull,
                    "zh_short" => ShortDisplay(tag, entityIdentity, shortNames) ?? zhFull,
                    _ => zhFull,
                };
            }
            tags[i] = tag with { DisplayOverride = display };
        }
        return tags;
    }

    private static string? ShortDisplay(TagInfo tag,
        Dictionary<string, string>? entityIdentity, Dictionary<string, string>? shortNames)
    {
        var identity = tag.IdentityKey;
        if (identity.Length == 0 && entityIdentity is not null &&
            tag.EntityKey.Length > 0 && entityIdentity.TryGetValue(tag.EntityKey, out var mapped))
            identity = mapped;
        if (identity.Length > 0 && shortNames is not null &&
            shortNames.TryGetValue(identity, out var shortName) && shortName.Length > 0)
            return shortName;

        var candidates = new List<string>();
        if (tag.ZhName.Length > 0) candidates.Add(tag.ZhName);
        if (tag.EnName.Length > 0) candidates.Add(tag.EnName);
        candidates.AddRange(tag.Aliases);
        if (tag.Name.Length > 0) candidates.Add(tag.Name);
        return candidates
            .OrderBy(s => s.Length)
            .ThenBy(s => s, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>当前全局标签显示模式；设置页和候选搜索共用。</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public string GlobalTagDisplayMode() => TagDisplayState.GlobalMode(_conn);

    /// <summary>保存全局标签显示模式；下一次 AllTags/搜索立即生效。</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetGlobalTagDisplayMode(string mode) =>
        TagDisplayState.SetGlobalMode(_conn, mode);

    /// <summary>搜索候选的来源名语言；与标签贴纸显示设置分开。</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public string GlobalTagSearchNameMode() => TagDisplayState.SearchNameMode(_conn);

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetGlobalTagSearchNameMode(string mode) =>
        TagDisplayState.SetSearchNameMode(_conn, mode);

    /// <summary>
    /// 照搬 store.py search_tags：空格折叠成下划线后做包含匹配（名或别名），
    /// 排序 = 全等优先 → 使用数降序 → 分类 → 名称。
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<TagInfo> SearchTags(string text, int limit = 30)
    {
        var needle = FoldForMatch(text ?? "");
        var exact = (text ?? "").Trim();
        IEnumerable<TagInfo> seq = AllTags();
        if (needle.Length > 0)
        {
            seq = seq.Where(t => t.SearchTerms.Any(s => FoldForMatch(s).Contains(needle, StringComparison.Ordinal)));
        }
        return seq
            .OrderBy(t => t.SearchTerms
                .Any(s => string.Equals(s.Trim(), exact, StringComparison.OrdinalIgnoreCase)) ? 0 : 1)
            .ThenByDescending(t => t.Count)
            .ThenBy(t => t.Category, StringComparer.Ordinal)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    /// <summary>批量取图片的标签明细，对应 tag_details_for_images（按图分块，避免逐图查询）。</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public Dictionary<int, List<TagInfo>> TagDetailsForImages(IReadOnlyCollection<int> imageIds)
    {
        var result = new Dictionary<int, List<TagInfo>>();
        if (imageIds.Count == 0) return result;
        var byId = AllTags().ToDictionary(t => t.Id);
        var ids = imageIds.Distinct().ToList();
        const int chunk = 900;
        for (var off = 0; off < ids.Count; off += chunk)
        {
            var part = ids.Skip(off).Take(chunk).ToList();
            var marks = string.Join(",", part.Select((_, i) => $"$c{i}"));
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT it.image_id,it.tag_id FROM image_tags it " +
                              $"WHERE it.image_id IN ({marks}) ORDER BY it.image_id,it.tag_id";
            for (var i = 0; i < part.Count; i++) cmd.Parameters.AddWithValue($"$c{i}", part[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var imageId = r.GetInt32(0);
                if (!byId.TryGetValue(r.GetInt32(1), out var info)) continue;
                if (!result.TryGetValue(imageId, out var list)) result[imageId] = list = [];
                if (list.All(x => x.Id != info.Id)) list.Add(info);
            }
        }
        return result;
    }

    /// <summary>批量移除若干标签名，返回删除的连接数，对应 remove_tags。</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public int RemoveTags(IReadOnlyCollection<int> imageIds, IEnumerable<string> names)
    {
        var cleaned = names.Select(n => (n ?? "").Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (imageIds.Count == 0 || cleaned.Count == 0) return 0;

        var idList = imageIds.ToList();
        var idMarks = string.Join(",", idList.Select((_, i) => $"$id{i}"));
        var nameMarks = string.Join(",", cleaned.Select((_, i) => $"$nm{i}"));
        int affected;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText =
                $"DELETE FROM image_tags WHERE image_id IN ({idMarks}) AND tag_id IN " +
                $"(SELECT id FROM tags WHERE name COLLATE NOCASE IN ({nameMarks}))";
            for (var i = 0; i < idList.Count; i++) cmd.Parameters.AddWithValue($"$id{i}", idList[i]);
            for (var i = 0; i < cleaned.Count; i++) cmd.Parameters.AddWithValue($"$nm{i}", cleaned[i]);
            affected = cmd.ExecuteNonQuery();
        }
        Commit();
        if (affected > 0) SyncSidecars(imageIds);
        return affected;
    }

    /// <summary>整体替换成给定标签集（全部 custom 分类），对应 set_tags。</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetTags(IReadOnlyCollection<int> imageIds, IEnumerable<string> names)
        => SetCategorizedTags(imageIds, names.Select(n => ("custom", n)).ToList());

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetFavorite(int imageId, bool favorite)
    {
        using (var cmd = _conn.CreateCommand())
        {
            if (favorite)
            {
                cmd.CommandText =
                    "INSERT INTO favorites(image_id,added_at) VALUES($i,$t) ON CONFLICT(image_id) DO NOTHING";
                cmd.Parameters.AddWithValue("$i", imageId);
                cmd.Parameters.AddWithValue("$t", Now());
            }
            else
            {
                cmd.CommandText = "DELETE FROM favorites WHERE image_id=$i";
                cmd.Parameters.AddWithValue("$i", imageId);
            }
            cmd.ExecuteNonQuery();
        }
        Commit();
        SyncSidecars([imageId]);
    }

    /// <summary>手动修改单图来源；空字符串按“清除该字段”写入。</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetImageOrigins(int imageId, string? pageUrl, string? sourceUrl)
    {
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE images SET page_url=$p,source_url=$s WHERE id=$i";
            cmd.Parameters.AddWithValue("$p", string.IsNullOrWhiteSpace(pageUrl) ? DBNull.Value : pageUrl.Trim());
            cmd.Parameters.AddWithValue("$s", string.IsNullOrWhiteSpace(sourceUrl) ? DBNull.Value : sourceUrl.Trim());
            cmd.Parameters.AddWithValue("$i", imageId);
            cmd.ExecuteNonQuery();
        }
        Commit();
        SyncSidecars([imageId]);
    }

    // ---------------------------------------------------------------- sidecar

    /// <summary>对应 _sync_sidecars：按 (root,dir) 去重后重写 sidecar。</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SyncSidecars(IReadOnlyCollection<int> imageIds)
    {
        if (!SidecarEnabled) return;
        var pairs = new HashSet<(int, string)>();
        foreach (var id in imageIds)
        {
            var f = FolderOf(id);
            if (f is not null) pairs.Add(f.Value);
        }
        foreach (var (rootId, dirRel) in pairs)
            WriteSidecar(rootId, dirRel);
    }

    /// <summary>对应 write_sidecar：整目录镜像到 .gallery.json（含未索引文件的既有条目保留）。</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool WriteSidecar(int rootId, string dirRel)
    {
        if (!SidecarEnabled) return false;
        var root = RootPath(rootId);
        if (root is null) return false;
        var folder = dirRel.Length > 0 ? Path.Combine(root, dirRel.Replace('/', '\\')) : root;
        if (!Directory.Exists(folder)) return false;

        var entries = SidecarEntries(rootId, dirRel);
        var sp = Path.Combine(folder, SidecarName);
        Dictionary<string, JsonElement>? existing = null;
        if (File.Exists(sp))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(sp));
                if (doc.RootElement.TryGetProperty("files", out var files) &&
                    files.ValueKind == JsonValueKind.Object)
                    existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                        files.GetRawText());
            }
            catch (Exception ex) when (ex is IOException or JsonException) { /* 视为无 sidecar */ }
        }

        var indexedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM images WHERE root_id=$r AND dir_rel=$d";
            cmd.Parameters.AddWithValue("$r", rootId);
            cmd.Parameters.AddWithValue("$d", dirRel);
            using var r = cmd.ExecuteReader();
            while (r.Read()) indexedNames.Add(r.GetString(0));
        }

        var merged = new Dictionary<string, object>(entries);
        if (existing is not null)
        {
            foreach (var (name, element) in existing)
                if (!indexedNames.Contains(name) && File.Exists(Path.Combine(folder, name)))
                    merged[name] = element;
        }

        if (merged.Count == 0)
        {
            if (File.Exists(sp))
            {
                try { File.Delete(sp); } catch (IOException) { }
                Exec($"DELETE FROM sidecar_state WHERE path='{Esc(sp)}'");
                Commit();
            }
            return false;
        }

        var payload = new Dictionary<string, object>
        {
            ["schema"] = SidecarSchema,
            ["updated_at"] = Math.Round(Now(), 3),
            ["files"] = merged,
        };

        // 内容一致 → 只刷新已知 mtime，避免来回覆盖
        if (existing is not null && SidecarEquals(existing, merged))
        {
            LastSidecarError = "";
            Exec($"DELETE FROM sync_errors WHERE path='{Esc(sp)}'");
            Exec($"INSERT INTO sidecar_state(path,mtime) VALUES('{Esc(sp)}',{Timestamp(sp)}) " +
                 "ON CONFLICT(path) DO UPDATE SET mtime=excluded.mtime");
            Commit();
            return false;
        }

        double mtime;
        try
        {
            mtime = AtomicWriteJson(sp, payload);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastSidecarError = ex.Message;
            Exec($"INSERT INTO sync_errors VALUES('{Esc(sp)}','{Esc(ex.Message)}',{Now()}) " +
                 "ON CONFLICT(path) DO UPDATE SET message=excluded.message,updated_at=excluded.updated_at");
            Commit();
            return false;
        }
        LastSidecarError = "";
        Exec($"DELETE FROM sync_errors WHERE path='{Esc(sp)}'");
        if (HideSidecar) HideFile(sp, true);
        Exec($"INSERT INTO sidecar_state(path,mtime) VALUES('{Esc(sp)}',{Fmt(mtime)}) " +
             "ON CONFLICT(path) DO UPDATE SET mtime=excluded.mtime");
        Commit();
        return true;
    }

    private static bool SidecarEquals(Dictionary<string, JsonElement> existing, Dictionary<string, object> merged)
        => JsonElement.DeepEquals(JsonSerializer.SerializeToElement(existing),
            JsonSerializer.SerializeToElement(merged));

    /// <summary>对应 _atomic_write_json：UTF-8 无 BOM / indent=2 / 键排序 / .tmp 原子替换。</summary>
    private static double AtomicWriteJson(string path, Dictionary<string, object> payload)
    {
        var options = new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var tmp = path + ".tmp";
        using (var stream = File.Create(tmp))
        using (var writer = new Utf8JsonWriter(stream, options))
            WriteSortedJson(writer, JsonSerializer.SerializeToElement(payload));
        File.Move(tmp, path, overwrite: true);
        return ToUnixSeconds(File.GetLastWriteTimeUtc(path));
    }

    private static void WriteSortedJson(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteSortedJson(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteSortedJson(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    private static void HideFile(string path, bool hide)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            var target = hide ? attrs | FileAttributes.Hidden : attrs & ~FileAttributes.Hidden;
            if (target != attrs) File.SetAttributes(path, target);
        }
        catch (IOException) { }
    }

    /// <summary>对应 _sidecar_entries：标签带分类前缀 + 别名 + 溯源字段。</summary>
    private Dictionary<string, object> SidecarEntries(int rootId, string dirRel)
    {
        var rows = QueryAll(
            "SELECT id,name,rating,image_uuid,managed,orig_md5,content_md5,page_url,source_url,nsfw,post_id " +
            "FROM images WHERE root_id=$r AND dir_rel=$d AND missing=0 ORDER BY name COLLATE NOCASE",
            ("$r", rootId), ("$d", dirRel));
        if (rows.Count == 0) return [];

        var ids = rows.Select(r => r.Id).ToList();
        var pairs = new Dictionary<int, HashSet<string>>();
        const int chunkSize = 900;
        for (var i = 0; i < ids.Count; i += chunkSize)
        {
            var chunk = ids.Skip(i).Take(chunkSize).ToList();
            var marks = string.Join(",", chunk.Select((_, j) => $"$c{j}"));
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "SELECT it.image_id, t.name, COALESCE(t.category,'custom') AS category " +
                "FROM image_tags it JOIN tags t ON t.id=it.tag_id " +
                $"WHERE it.image_id IN ({marks})";
            for (var j = 0; j < chunk.Count; j++)
                cmd.Parameters.AddWithValue($"$c{j}", chunk[j]);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var imageId = r.GetInt32(0);
                if (!pairs.TryGetValue(imageId, out var set))
                    pairs[imageId] = set = [];
                set.Add(Taxonomy.FullName(r.GetString(2), r.GetString(1)));
            }
        }

        var output = new Dictionary<string, object>();
        foreach (var row in rows)
        {
            var tags = pairs.TryGetValue(row.Id, out var set)
                ? set.OrderBy(x => x, StringComparer.Ordinal).ToList()
                : new List<string>();
            var rating = row.Rating;
            if (tags.Count == 0 && rating == 0 && !row.Managed) continue;

            var entry = new Dictionary<string, object> { ["tags"] = tags };
            if (row.ImageUuid is not null) entry["image_uuid"] = row.ImageUuid;

            var aliases = new Dictionary<string, object>();
            var classified = new List<object>();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT t.id,t.name,t.category FROM tags t " +
                    "JOIN image_tags it ON it.tag_id=t.id WHERE it.image_id=$i";
                cmd.Parameters.AddWithValue("$i", row.Id);
                using var r = cmd.ExecuteReader();
                var tagRows = new List<(int Id, string Name, string Category)>();
                while (r.Read())
                    tagRows.Add((r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? "custom" : r.GetString(2)));
                foreach (var tag in tagRows)
                {
                    var names = new List<string>();
                    using (var a = _conn.CreateCommand())
                    {
                        a.CommandText = "SELECT alias FROM tag_aliases WHERE tag_id=$t";
                        a.Parameters.AddWithValue("$t", tag.Id);
                        using var ar = a.ExecuteReader();
                        while (ar.Read()) names.Add(ar.GetString(0));
                    }
                    classified.Add(new SortedDictionary<string, object>
                    {
                        ["category"] = tag.Category,
                        ["name"] = tag.Name,
                        ["aliases"] = names,
                    });
                    if (names.Count > 0)
                        aliases[Taxonomy.FullName(tag.Category, tag.Name)] = names;
                }
            }
            if (aliases.Count > 0) entry["aliases"] = aliases;
            entry["classified_tags"] = classified
                .OrderBy(x => ((SortedDictionary<string, object>)x)["category"])
                .ThenBy(x => ((SortedDictionary<string, object>)x)["name"])
                .ToList();

            var manual = new List<string>();
            using (var m = _conn.CreateCommand())
            {
                m.CommandText = "SELECT category FROM image_manual_categories WHERE image_id=$i";
                m.Parameters.AddWithValue("$i", row.Id);
                using var mr = m.ExecuteReader();
                while (mr.Read()) manual.Add(mr.GetString(0));
            }
            entry["manual_categories"] = manual;

            var origin = new Dictionary<string, object>();
            foreach (var (key, value) in new (string, string?)[] {
                ("orig_md5", row.OrigMd5), ("content_md5", row.ContentMd5),
                ("page_url", row.PageUrl), ("source_url", row.SourceUrl),
                ("nsfw", row.Nsfw), ("post_id", row.PostId) })
            {
                if (!string.IsNullOrEmpty(value)) origin[key] = value;
            }
            if (origin.Count > 0) entry["origin"] = origin;
            if (rating != 0) entry["rating"] = rating;

            output[row.Name] = entry;
        }
        return output;
    }

    /// <summary>对应 import_sidecar：外部改动的 sidecar → 导入库。返回导入文件数。</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public int ImportSidecar(int rootId, string dirRel)
    {
        if (!SidecarEnabled) return 0;
        var root = RootPath(rootId);
        if (root is null) return 0;
        var folder = dirRel.Length > 0 ? Path.Combine(root, dirRel.Replace('/', '\\')) : root;
        var sp = Path.Combine(folder, SidecarName);
        if (!File.Exists(sp)) return 0;

        double mtime;
        try { mtime = Timestamp(sp); } catch (IOException) { return 0; }
        var known = SidecarKnownMtime(sp);
        if (known is not null && Math.Abs(known.Value - mtime) < 1e-6) return 0;

        Dictionary<string, JsonElement> files;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(sp));
            if (!doc.RootElement.TryGetProperty("files", out var f) ||
                f.ValueKind != JsonValueKind.Object) return 0;
            files = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(f.GetRawText()) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return 0; }

        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT id,name FROM images WHERE root_id=$r AND dir_rel=$d AND missing=0";
            cmd.Parameters.AddWithValue("$r", rootId);
            cmd.Parameters.AddWithValue("$d", dirRel);
            using var r = cmd.ExecuteReader();
            while (r.Read()) byName[r.GetString(1)] = r.GetInt32(0);
        }

        using var transaction = Transaction();
        var imported = 0;
        foreach (var (fname, element) in files)
        {
            if (!byName.TryGetValue(fname, out var imageId)) continue;
            var tags = new List<string>();
            if (element.TryGetProperty("tags", out var tv) && tv.ValueKind == JsonValueKind.Array)
                foreach (var t in tv.EnumerateArray())
                    if (t.ValueKind == JsonValueKind.String) tags.Add(t.GetString() ?? "");

            var parsed = new List<(string Cat, string Name, List<string> Aliases)>();
            var typed = element.TryGetProperty("classified_tags", out var ctv) &&
                        ctv.ValueKind == JsonValueKind.Array && ctv.GetArrayLength() > 0;
            if (typed)
            {
                foreach (var t in ctv.EnumerateArray())
                {
                    var cat = t.TryGetProperty("category", out var c) ? c.GetString() : null;
                    var nm = t.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (cat is null || nm is null || !Taxonomy.IsValidCategory(cat))
                    {
                        typed = false;
                        break;
                    }
                    var al = new List<string>();
                    if (t.TryGetProperty("aliases", out var av) && av.ValueKind == JsonValueKind.Array)
                        foreach (var x in av.EnumerateArray())
                            if (x.ValueKind == JsonValueKind.String) al.Add(x.GetString() ?? "");
                    parsed.Add((cat, nm, al));
                }
                if (typed)
                {
                    var lhs = parsed.Select(p => Taxonomy.FullName(p.Cat, p.Name))
                        .OrderBy(x => x, StringComparer.Ordinal).ToList();
                    var rhs = tags.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                    if (lhs.Count != rhs.Count || lhs.Zip(rhs).Any(p => p.First != p.Second)) typed = false;
                }
            }
            if (!typed)
            {
                parsed = tags.Select(raw =>
                {
                    var (cat, name) = Taxonomy.SplitFullName(raw);
                    var al = new List<string>();
                    if (element.TryGetProperty("aliases", out var av) &&
                        av.ValueKind == JsonValueKind.Object &&
                        av.TryGetProperty(raw, out var aliasFor))
                        foreach (var x in aliasFor.EnumerateArray())
                            if (x.ValueKind == JsonValueKind.String) al.Add(x.GetString() ?? "");
                    return (cat, name, al);
                }).ToList();
            }

            var tagIds = new List<int>();
            var seen = new HashSet<int>();
            foreach (var (cat, name, aliasValues) in parsed)
            {
                if (name.Length == 0) continue;
                var tid = EnsureTag(name, cat);
                foreach (var alias in aliasValues)
                    if (alias.Trim().Length > 0)
                        Exec($"INSERT OR IGNORE INTO tag_aliases VALUES({tid},'{Esc(alias.Trim())}')");
                if (seen.Add(tid)) tagIds.Add(tid);
            }

            Exec($"DELETE FROM image_tags WHERE image_id={imageId}");
            foreach (var tid in tagIds)
                Exec($"INSERT OR IGNORE INTO image_tags(image_id,tag_id) VALUES({imageId},{tid})");

            if (element.TryGetProperty("rating", out var rv) && rv.ValueKind == JsonValueKind.Number)
            {
                var rating = Math.Clamp(rv.GetInt32(), 0, 5);
                Exec($"UPDATE images SET rating={rating} WHERE id={imageId}");
            }
            if (element.TryGetProperty("image_uuid", out var uv) && uv.ValueKind == JsonValueKind.String)
            {
                var uuid = uv.GetString() ?? "";
                if (uuid.Length > 0)
                {
                    using var cmd = _conn.CreateCommand();
                    cmd.CommandText =
                        "SELECT 1 FROM images WHERE image_uuid=$u AND id!=$i";
                    cmd.Parameters.AddWithValue("$u", uuid);
                    cmd.Parameters.AddWithValue("$i", imageId);
                    if (cmd.ExecuteScalar() is null)
                        Exec($"UPDATE images SET image_uuid='{Esc(uuid)}',managed=1 WHERE id={imageId}");
                }
            }
            if (element.TryGetProperty("origin", out var ov) && ov.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "orig_md5", "content_md5", "page_url", "source_url", "nsfw", "post_id" })
                {
                    if (ov.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        var v = value.GetString() ?? "";
                        if (v.Length > 0)
                            Exec($"UPDATE images SET {key}=COALESCE(NULLIF({key},''),'{Esc(v)}') WHERE id={imageId}");
                    }
                }
            }
            if (element.TryGetProperty("manual_categories", out var mv) && mv.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in mv.EnumerateArray())
                {
                    var category = c.GetString();
                    if (category is not null && Taxonomy.IsValidCategory(category))
                        Exec($"INSERT OR IGNORE INTO image_manual_categories VALUES({imageId},'{category}')");
                }
            }
            imported++;
        }
        Commit();
        if (imported > 0)
            Exec($"INSERT INTO sidecar_state(path,mtime) VALUES('{Esc(sp)}',{Fmt(mtime)}) " +
                 "ON CONFLICT(path) DO UPDATE SET mtime=excluded.mtime");
        transaction.Success = true;
        return imported;
    }

    private double? SidecarKnownMtime(string path)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT mtime FROM sidecar_state WHERE path=$p";
        cmd.Parameters.AddWithValue("$p", path);
        var v = cmd.ExecuteScalar();
        return v is null ? null : Convert.ToDouble(v);
    }

    // -------------------------------------------------------------- utilities

    private static double Now() =>
        DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalSeconds;

    private static double Timestamp(string path) =>
        ToUnixSeconds(File.GetLastWriteTimeUtc(path));

    /// <summary>对应 Python f"{x:.9f}" 的秒值格式（避开浮点默认输出差异）。</summary>
    // 注意：必须用标准格式 "F9"。自定义格式 "0.000000000" 只保留 14 位有效数字，
    // 会在小数第 6 位截断（实测 1792232839.8936038 → .893600000），导致与 Python 不一致。
    public static string Fmt(double seconds) => seconds.ToString("F9",
        System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// FILETIME → Unix 秒，逐步骤复刻 CPython os.stat 的浮点路径：
    /// 整秒（整数运算）+ 小数（余数/1e7）。整体除法会在第 9 位小数分叉，
    /// 导致缩略图 key / sidecar mtime 判定不一致。
    /// </summary>
    public static double ToUnixSeconds(DateTime utc)
    {
        long ft = utc.ToFileTimeUtc();
        long sec = ft / 10_000_000 - 11644473600;
        double frac = (ft % 10_000_000) / 10_000_000.0;
        return sec + frac;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void Dispose() => _conn.Dispose();
}

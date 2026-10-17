using Microsoft.Data.Sqlite;
using System.Text.Json;

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

    public string DbPath { get; }
    public bool SidecarEnabled { get; set; } = true;
    public bool HideSidecar { get; set; } = true;
    public List<string> TrackingNotes { get; } = [];
    public HashSet<(int RootId, string DirRel)> TrackingDirty { get; } = [];
    public string LastSidecarError { get; set; } = "";

    public StoreService(string dbPath)
    {
        DbPath = dbPath;
        var db = new Db(dbPath);
        _conn = db.Connection;
    }

    public int SchemaVersion
    {
        get
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key='schema_version'";
            return int.TryParse(cmd.ExecuteScalar() as string, out var n) ? n : 0;
        }
    }

    public void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>自动提交模式下语句即时持久化；保留方法名与 galleryold 对齐。</summary>
    public void Commit() { }

    /// <summary>
    /// 对应 store.py transaction()：外层 BEGIN IMMEDIATE，可嵌套；
    /// 成功置 <see cref="TxScope.Success"/>，异常时 Dispose 回滚（对齐 Python with 语义）。
    /// </summary>
    public TxScope Transaction() => new(this);

    public sealed class TxScope(StoreService s) : IDisposable
    {
        public bool Success { get; set; }

        public void Dispose()
        {
            s._inTransaction--;
            if (s._inTransaction == 0)
            {
                if (Success) s.Exec("COMMIT");
                else { try { s.Exec("ROLLBACK"); } catch (SqliteException) { } }
            }
        }
    }

    // ------------------------------------------------------------------ roots

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

    public ImageRow? GetImage(int id) =>
        QueryOne("SELECT * FROM images WHERE id=$i", ("$i", id));

    public ImageRow? QueryOne(string sql, params (string, object)[] args)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadImage(r) : null;
    }

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

    public int CountImages(bool includeMissing = false)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = includeMissing
            ? "SELECT COUNT(*) FROM images"
            : "SELECT COUNT(*) FROM images WHERE missing=0";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int CountFavorites()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM favorites";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

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
    }

    private static string LikeEscape(string s)
        => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// 对应 store.py query_images（批次 1 范围：目录/收藏/搜索/8 键排序）。
    /// 排序 SQL 逐字照搬 _SORT_SQL + name 特例（字母组 CASE 前置 + GALLERY_NATURAL）
    /// + 非 name 的稳定尾排序。
    /// </summary>
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
        var search = spec.Search.Trim();
        if (search.Length > 0)
        {
            // galleryold 语义：标签 / 中文别名 / 文件名（别名经 tag_aliases 命中标签）
            var ph = $"$p{args.Count}";
            args.Add("%" + LikeEscape(search) + "%");
            where.Add(
                $"(i.name LIKE {ph} ESCAPE '\\' OR EXISTS(" +
                "SELECT 1 FROM image_tags it JOIN tags t ON t.id=it.tag_id " +
                $"WHERE it.image_id=i.id AND (t.name LIKE {ph} ESCAPE '\\' " +
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
                "mtime" => "i.mtime",
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

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO tags(name,category) VALUES($n,$c); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$n", clean);
            cmd.Parameters.AddWithValue("$c", category);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

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

    // ---------------------------------------------------------------- sidecar

    /// <summary>对应 _sync_sidecars：按 (root,dir) 去重后重写 sidecar。</summary>
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
    {
        if (existing.Count != merged.Count) return false;
        foreach (var (name, element) in existing)
        {
            if (!merged.TryGetValue(name, out var value)) return false;
            var a = JsonSerializer.Serialize(element);
            var b = JsonSerializer.Serialize(value);
            if (a != b) return false;
        }
        return true;
    }

    /// <summary>对应 _atomic_write_json：UTF-8 无 BOM / indent=2 / 键排序 / .tmp 原子替换。</summary>
    private static double AtomicWriteJson(string path, Dictionary<string, object> payload)
    {
        var ordered = new SortedDictionary<string, object>(payload, StringComparer.Ordinal);
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = null,
        };
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(ordered, options),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
        return ToUnixSeconds(File.GetLastWriteTimeUtc(path));
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

    public void Dispose() => _conn.Dispose();
}

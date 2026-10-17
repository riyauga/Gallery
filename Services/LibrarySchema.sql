-- galleryold app/store.py _DDL + _MIGRATIONS and app/tag_catalog.py DDL.
-- Only used when initializing a completely empty database; never run on an existing library.

CREATE TABLE IF NOT EXISTS meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS roots (
    id       INTEGER PRIMARY KEY,
    path     TEXT NOT NULL UNIQUE,
    added_at REAL NOT NULL
);

CREATE TABLE IF NOT EXISTS images (
    id       INTEGER PRIMARY KEY,
    root_id  INTEGER NOT NULL REFERENCES roots(id) ON DELETE CASCADE,
    rel_path TEXT NOT NULL,          -- 相对 root 的路径，正斜杠
    abs_path TEXT NOT NULL,
    dir_rel  TEXT NOT NULL,          -- 相对 root 的目录，正斜杠；根目录为 ''
    name     TEXT NOT NULL,
    ext      TEXT NOT NULL,
    size     INTEGER NOT NULL,
    mtime    REAL NOT NULL,
    ctime    REAL,
    taken_at REAL,
    width    INTEGER,
    height   INTEGER,
    rating   INTEGER NOT NULL DEFAULT 0,
    missing  INTEGER NOT NULL DEFAULT 0,
    added_at REAL NOT NULL,
    updated_at REAL NOT NULL,
    -- v2：danbooru 溯源与元数据
    orig_md5    TEXT,                -- 文件名里那段 md5（溯源钥匙，永不改写）
    content_md5 TEXT,                -- 实际算出来的内容 md5
    page_url    TEXT,                -- 帖子页地址
    source_url  TEXT,                -- 原帖出处链接
    nsfw        TEXT,                -- danbooru 四级分级 g/s/q/e
    post_id     TEXT,                -- danbooru 帖子 id
    UNIQUE(root_id, rel_path)
);

CREATE INDEX IF NOT EXISTS idx_images_dir    ON images(root_id, dir_rel);
CREATE INDEX IF NOT EXISTS idx_images_mtime  ON images(mtime);
CREATE INDEX IF NOT EXISTS idx_images_taken  ON images(taken_at);
CREATE INDEX IF NOT EXISTS idx_images_name   ON images(name COLLATE NOCASE);

CREATE TABLE IF NOT EXISTS tags (
    id       INTEGER PRIMARY KEY,
    name     TEXT NOT NULL COLLATE NOCASE,
    color    TEXT,
    category TEXT NOT NULL DEFAULT 'custom',
    UNIQUE(category, name)
);

CREATE TABLE IF NOT EXISTS image_tags (
    image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
    tag_id   INTEGER NOT NULL REFERENCES tags(id)   ON DELETE CASCADE,
    PRIMARY KEY (image_id, tag_id)
);

CREATE INDEX IF NOT EXISTS idx_image_tags_tag ON image_tags(tag_id);

CREATE TABLE IF NOT EXISTS sidecar_state (
    path  TEXT PRIMARY KEY,
    mtime REAL NOT NULL,
    hash  TEXT
);

-- 插件通过本地桥送来的「页面标签」，等桌面端与 _inbox 里的文件配对
CREATE TABLE IF NOT EXISTS ingest_queue (
    id         INTEGER PRIMARY KEY,
    received_at REAL NOT NULL,
    state      TEXT NOT NULL DEFAULT 'pending',   -- pending | done | error
    inbox_path TEXT,        -- 下载到的绝对路径
    inbox_name TEXT,        -- 文件名
    page_url   TEXT,
    image_url  TEXT,
    post_id    TEXT,
    md5        TEXT,
    rating     TEXT,
    source     TEXT,
    payload    TEXT NOT NULL,   -- 完整原始 JSON，向前兼容
    note       TEXT
);

CREATE INDEX IF NOT EXISTS idx_ingest_state ON ingest_queue(state, received_at);

CREATE TABLE IF NOT EXISTS tag_aliases (
    tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
    alias TEXT NOT NULL COLLATE NOCASE,
    PRIMARY KEY(tag_id, alias)
);
CREATE TABLE IF NOT EXISTS review_drafts (
    path_key TEXT PRIMARY KEY,
    payload TEXT NOT NULL,
    state TEXT NOT NULL DEFAULT 'pending',
    updated_at REAL NOT NULL
);
CREATE TABLE IF NOT EXISTS archive_jobs (
    id TEXT PRIMARY KEY,
    source TEXT NOT NULL,
    destination TEXT NOT NULL,
    phase TEXT NOT NULL,
    payload TEXT NOT NULL,
    error TEXT NOT NULL DEFAULT '',
    updated_at REAL NOT NULL
);
CREATE TABLE IF NOT EXISTS sync_errors (
    path TEXT PRIMARY KEY,
    message TEXT NOT NULL,
    updated_at REAL NOT NULL
);
CREATE TABLE IF NOT EXISTS image_manual_categories (
    image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
    category TEXT NOT NULL,
    PRIMARY KEY(image_id,category)
);
CREATE TABLE IF NOT EXISTS favorites (
    image_id INTEGER PRIMARY KEY REFERENCES images(id) ON DELETE CASCADE,
    added_at REAL NOT NULL
);

-- 敏感内容判定分数：一图一行，重判定时覆盖
CREATE TABLE IF NOT EXISTS nsfw_scores (
    image_id   INTEGER PRIMARY KEY REFERENCES images(id) ON DELETE CASCADE,
    source     TEXT NOT NULL,                -- danbooru | model
    rating     TEXT NOT NULL,                -- 原始四级 g/s/q/e
    value      TEXT NOT NULL DEFAULT '',     -- 原始依据：官方分级串 / 模型命中的标签名
    nsfw_prob  REAL NOT NULL DEFAULT 0,      -- 模型四类里 NSFW 三档概率之和
    confidence REAL NOT NULL DEFAULT 0,      -- 最高那类的概率
    margin     REAL NOT NULL DEFAULT 0,      -- 最高与次高之差，越小越模棱两可
    model_name TEXT NOT NULL DEFAULT '',
    model_hash TEXT NOT NULL DEFAULT '',     -- 模型指纹，换模型后便于只重跑旧结果
    detail     TEXT NOT NULL DEFAULT '',     -- JSON：四类概率等
    checked_at REAL NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_nsfw_source ON nsfw_scores(source);


CREATE TABLE IF NOT EXISTS catalog_games(game_key TEXT PRIMARY KEY,zh TEXT NOT NULL,en TEXT NOT NULL,aliases TEXT NOT NULL,source_url TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS catalog_entities(entity_key TEXT PRIMARY KEY,identity_key TEXT NOT NULL,game_key TEXT NOT NULL,kind TEXT NOT NULL,zh TEXT NOT NULL,en TEXT NOT NULL,aliases TEXT NOT NULL,status TEXT NOT NULL,source_url TEXT NOT NULL,source_kind TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS catalog_names(term TEXT NOT NULL,entity_key TEXT NOT NULL REFERENCES catalog_entities(entity_key) ON DELETE CASCADE,PRIMARY KEY(term,entity_key));
CREATE TABLE IF NOT EXISTS catalog_source_tags(tag TEXT NOT NULL,entity_key TEXT NOT NULL REFERENCES catalog_entities(entity_key) ON DELETE CASCADE,PRIMARY KEY(tag,entity_key));
CREATE TABLE IF NOT EXISTS catalog_memberships(identity_key TEXT NOT NULL,game_key TEXT NOT NULL,source_url TEXT NOT NULL,PRIMARY KEY(identity_key,game_key));
CREATE TABLE IF NOT EXISTS tag_profiles(tag_id INTEGER PRIMARY KEY REFERENCES tags(id) ON DELETE CASCADE,zh TEXT NOT NULL DEFAULT '',en TEXT NOT NULL DEFAULT '',entity_key TEXT NOT NULL DEFAULT '');
CREATE TABLE IF NOT EXISTS catalog_imports(source_id TEXT PRIMARY KEY,digest TEXT NOT NULL,report TEXT NOT NULL);

ALTER TABLE roots ADD COLUMN enabled INTEGER NOT NULL DEFAULT 1;
ALTER TABLE images ADD COLUMN image_uuid TEXT;
ALTER TABLE images ADD COLUMN file_key TEXT;
ALTER TABLE images ADD COLUMN managed INTEGER NOT NULL DEFAULT 0;
ALTER TABLE ingest_queue ADD COLUMN ingest_id TEXT;
ALTER TABLE ingest_queue ADD COLUMN path_key TEXT;
CREATE UNIQUE INDEX IF NOT EXISTS idx_ingest_id ON ingest_queue(ingest_id) WHERE ingest_id IS NOT NULL AND ingest_id!='';
CREATE INDEX IF NOT EXISTS idx_ingest_path ON ingest_queue(path_key);
CREATE UNIQUE INDEX IF NOT EXISTS idx_image_uuid ON images(image_uuid);
CREATE INDEX IF NOT EXISTS idx_file_key ON images(file_key);
INSERT INTO meta(key,value) VALUES('schema_version','3');

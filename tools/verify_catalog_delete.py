"""资料库删除回归验证：只读真实库，检查 schema、索引与批量预览一致性。

用法：python tools/verify_catalog_delete.py
退出码 0 = 全部通过；1 = 有失败项。不修改任何数据。
"""
import json
import os
import sqlite3
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DB = os.path.join(os.environ["LOCALAPPDATA"], "Gallery", "library.db")
SOURCE = os.path.join(ROOT, "Services", "TagCatalogService.Library.cs")
SAMPLE = 25

TAGS_SQL = """
WITH entities AS MATERIALIZED (SELECT * FROM catalog_effective_entities WHERE kind=:kind)
SELECT t.id,t.name,(SELECT COUNT(*) FROM image_tags it WHERE it.tag_id=t.id),
       COALESCE(NULLIF(ti.identity_key,''),e.identity_key,''),COALESCE(NULLIF(g.zh,''),NULLIF(g.en,''),NULLIF(e.zh,''),NULLIF(e.en,''),''),
       t.category,COALESCE(t.tag_key,''),
       COALESCE(CASE WHEN t.category='work' THEN e.game_key
           ELSE COALESCE(NULLIF(ti.identity_key,''),e.identity_key) END,'')
FROM tags t LEFT JOIN tag_identities ti ON ti.tag_id=t.id
LEFT JOIN tag_profiles f ON f.tag_id=t.id
LEFT JOIN entities e ON COALESCE(ti.link_status,'')<>'independent' AND e.kind=t.category
    AND e.entity_key=COALESCE(
    (SELECT p.entity_key FROM entities p WHERE p.entity_key=f.entity_key AND p.kind=t.category
       AND (COALESCE(ti.identity_key,'')='' OR p.identity_key=ti.identity_key)),
    (SELECT a.entity_key FROM entities a WHERE a.identity_key=ti.identity_key AND a.kind=t.category
       ORDER BY (a.entity_key=ti.identity_key) DESC,a.entity_key LIMIT 1))
LEFT JOIN catalog_effective_games g ON t.category='work' AND g.game_key=e.game_key
WHERE t.category=:kind AND (:key IS NULL OR
    CASE WHEN t.category='work' THEN e.game_key ELSE COALESCE(NULLIF(ti.identity_key,''),e.identity_key) END=:key)
  AND (:keys IS NULL OR (CASE WHEN t.category='work' THEN e.game_key
       ELSE COALESCE(NULLIF(ti.identity_key,''),e.identity_key) END) IN (SELECT value FROM json_each(:keys)))
ORDER BY t.name COLLATE NOCASE,t.id
"""

ok = []


def check(condition, message):
    ok.append(bool(condition))
    print(("PASS  " if condition else "FAIL  ") + message)


def tags(con, kind, key=None, keys=None):
    params = {"kind": kind, "key": key,
              "keys": json.dumps(keys, ensure_ascii=False) if keys is not None else None}
    return [(row[0], row[7]) for row in con.execute(TAGS_SQL, params).fetchall()]


with open(SOURCE, encoding="utf-8") as stream:
    source = stream.read()
check("资料的关联已变化" not in source, "提交阶段已移除二次比对（该误报来源已删除）")

con = sqlite3.connect("file:" + DB + "?mode=ro", uri=True)
version = int(con.execute("SELECT value FROM meta WHERE key='schema_version'").fetchone()[0])
check(version >= 10, "schema v%d >= 10" % version)
indexed = con.execute(
    "SELECT 1 FROM sqlite_master WHERE type='index' AND name='idx_catalog_entities_kind_identity_game'"
).fetchone()
check(bool(indexed), "复合索引 idx_catalog_entities_kind_identity_game 已建立")

work = con.execute("SELECT game_key FROM catalog_effective_games LIMIT 1").fetchone()[0]
t0 = time.perf_counter()
con.execute("SELECT identity_key FROM catalog_character_origins WHERE game_key=? ORDER BY identity_key",
            (work,)).fetchall()
elapsed = (time.perf_counter() - t0) * 1000
check(elapsed < 500, "派生角色查询 %.0f ms < 500 ms（迁移已生效）" % elapsed)

# LibraryTags(kind) 无条目键：返回该类别全部标签，关联键可能为空，读取侧不能再遇到 NULL。
for kind in ("work", "character"):
    all_rows = con.execute(TAGS_SQL, {"kind": kind, "key": None, "keys": None}).fetchall()
    nulls = sum(1 for row in all_rows if row[7] is None)
    check(nulls == 0, "LibraryTags(%s) 全量 %d 行，NULL 关联键 %d" % (kind, len(all_rows), nulls))

works = [r[0] for r in con.execute("SELECT game_key FROM catalog_effective_games LIMIT %d" % SAMPLE)]
chars = [r[0] for r in con.execute(
    "SELECT identity_key FROM catalog_effective_entities WHERE kind='character' LIMIT %d" % SAMPLE)]
failures = []
for key in works:
    single_chars = [r[0] for r in con.execute(
        "SELECT identity_key FROM catalog_character_origins WHERE game_key=? ORDER BY identity_key", (key,))]
    batch_chars = [r[1] for r in con.execute(
        "SELECT game_key,identity_key FROM catalog_character_origins WHERE game_key IN (SELECT value FROM json_each(?)) "
        "ORDER BY game_key,identity_key", (json.dumps([key]),))]
    old = [t[0] for t in tags(con, "work", key=key)]
    if single_chars:
        old += [t[0] for t in tags(con, "character", keys=single_chars)]
    affected = set(single_chars)
    new = [t[0] for t in tags(con, "work", keys=[key]) if t[1] == key]
    if affected:
        new += [t[0] for t in tags(con, "character", keys=sorted(affected)) if t[1] in affected]
    if single_chars != batch_chars or old != new:
        failures.append(key)

for key in chars:
    old = [t[0] for t in tags(con, "character", key=key)]
    new = [t[0] for t in tags(con, "character", keys=[key]) if t[1] == key]
    if old != new:
        failures.append(key)

check(not failures, "预览对拍 %d 例零差异%s" % (len(works) + len(chars),
      "" if not failures else "（失败：" + ", ".join(failures[:3]) + "）"))
con.close()

print("\n结果：%d/%d 通过" % (sum(ok), len(ok)))
sys.exit(0 if all(ok) else 1)

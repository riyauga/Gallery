"""Export a v11/v12 catalog as a public v2 sharing ZIP, without personal settings."""
import argparse
from collections import defaultdict
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import tempfile
import uuid
import zipfile


def export_catalog(database: Path, destination: Path) -> dict:
    # SQLite backup captures WAL too; the user's database is opened read-only.
    source = sqlite3.connect(database.resolve().as_uri() + '?mode=ro', uri=True)
    snapshot = sqlite3.connect(':memory:')
    try:
        source.backup(snapshot)
    finally:
        source.close()
    try:
        version = snapshot.execute("SELECT value FROM meta WHERE key='schema_version'").fetchone()[0]
        if version not in ('11', '12'):
            raise ValueError('Catalog export requires schema v11 or v12; upgrade in Gallery first.')
        appearances = defaultdict(list)
        for character, work in snapshot.execute('SELECT character_entity_id,work_entity_id FROM catalog_appearances ORDER BY work_entity_id'):
            appearances[character].append(work)
        entries = [dict(entity_id=identity, kind=kind, zh=zh, en=en, aliases=json.loads(aliases),
                        origin=origin, appearances=appearances[identity], cover_hash=cover)
                   for identity, kind, zh, en, aliases, origin, cover in snapshot.execute(
                       'SELECT entity_id,kind,zh,en,aliases,origin_entity_id,cover_hash FROM catalog_entries ORDER BY id')]
        if not entries:
            raise ValueError('The catalog is empty.')
        identities = [dict(entity_id=identity, target_entity_id=target) for identity, target in snapshot.execute(
            'SELECT entity_id,target_entity_id FROM catalog_entity_ids ORDER BY entity_id')]
        pairs = [dict(entity_id_a=a, entity_id_b=b) for a, b in snapshot.execute(
            'SELECT entity_id_a,entity_id_b FROM catalog_distinct_pairs ORDER BY entity_id_a,entity_id_b')]
        assets = dict(snapshot.execute('SELECT hash,image FROM catalog_assets WHERE hash IN (SELECT cover_hash FROM catalog_entries)'))
    finally:
        snapshot.close()

    ids = {entry['entity_id'] for entry in entries}
    works = {entry['entity_id'] for entry in entries if entry['kind'] == 'work'}
    if len(ids) != len(entries) or any(str(uuid.UUID(identity)) != identity for identity in ids):
        raise ValueError('Invalid or duplicate entity IDs.')
    for entry in entries:
        if entry['origin'] is not None and entry['origin'] not in works or not set(entry['appearances']) <= works:
            raise ValueError('Missing work reference.')
        if entry['cover_hash'] is not None and entry['cover_hash'] not in assets:
            raise ValueError('Missing cover asset.')
    if any(item['target_entity_id'] not in ids for item in identities):
        raise ValueError('Invalid identity mapping.')
    for digest, data in assets.items():
        if not 3 <= len(data) <= 8 * 1024 * 1024 or hashlib.sha256(data).hexdigest() != digest:
            raise ValueError('Invalid cover asset.')

    repo = Path(__file__).resolve().parent.parent
    destination = destination.resolve()
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_name(destination.stem + '.' + uuid.uuid4().hex + '.tmp.zip')
    try:
        with tempfile.TemporaryDirectory(prefix='catalog-export-', dir=repo / 'obj') as staging:
            stage = Path(staging)
            for name, value in [('manifest.json', dict(format='gallery-catalog', version=2)),
                                ('entries.json', entries), ('entity_ids.json', identities), ('distinct_pairs.json', pairs)]:
                (stage / name).write_text(json.dumps(value, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
            names = ['manifest.json', 'entries.json', 'entity_ids.json', 'distinct_pairs.json']
            if assets:
                (stage / 'assets').mkdir()
                for digest, data in assets.items():
                    (stage / 'assets' / (digest + '.jpg')).write_bytes(data)
                names.append('assets')
            subprocess.run(['tar.exe', '-a', '-c', '-f', str(temporary), '-C', str(stage), *names], check=True)
        with zipfile.ZipFile(temporary) as archive:
            if archive.testzip() is not None:
                raise ValueError('Catalog ZIP integrity check failed.')
            files = [entry for entry in archive.infolist() if not entry.is_dir()]
            unpacked = sum(entry.file_size for entry in files)
        temporary.replace(destination)
    finally:
        temporary.unlink(missing_ok=True)
    return dict(works=len(works), characters=len(entries)-len(works), assets=len(assets),
                files=len(files), unpacked_size=unpacked, size=destination.stat().st_size,
                sha256=hashlib.sha256(destination.read_bytes()).hexdigest())


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--database', type=Path, default=Path(os.environ['LOCALAPPDATA']) / 'Gallery' / 'library.db')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(export_catalog(args.database, args.output), ensure_ascii=False))

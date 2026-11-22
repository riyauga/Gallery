namespace Gallery.Services;

public sealed partial class StoreService
{
    private readonly HashSet<int> _archivingIngestIds = [];

    // Subscribers only signal a refresh. They read the snapshot outside the store lock.
    public event Action? ReviewableIngestChanged;

    public int ArchivingIngestCount
    {
        get { lock (SyncRoot) return _archivingIngestIds.Count; }
    }

    public bool IsIngestArchiving(int id)
    {
        lock (SyncRoot) return _archivingIngestIds.Contains(id);
    }

    public void BeginIngestArchiving(IEnumerable<int> ids)
    {
        lock (SyncRoot)
        {
            _archivingIngestIds.UnionWith(ids);
            ReviewableIngestChanged?.Invoke();
        }
    }

    public void EndIngestArchiving(int id)
    {
        lock (SyncRoot)
        {
            if (_archivingIngestIds.Remove(id)) ReviewableIngestChanged?.Invoke();
        }
    }

    public int ReviewableIngestCount()
    {
        lock (SyncRoot)
        {
            if (_archivingIngestIds.Count == 0) return PendingIngestCount();

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM ingest_queue WHERE state='pending'";
            using var rows = cmd.ExecuteReader();
            var count = 0;
            while (rows.Read())
                if (!_archivingIngestIds.Contains(rows.GetInt32(0))) count++;
            return count;
        }
    }
}

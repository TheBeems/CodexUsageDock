using System.Text.Json;

namespace CodexUsageDock;

internal sealed class CachedCodexSessionReader
{
    private readonly string _homePath;
    private readonly long _maxBytesPerScan;
    private readonly int _maxCachedFiles;
    private readonly int _maxLineBytes;
    private readonly int _fileReadQuantum;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _sync = new();
    private readonly Dictionary<string, FileState> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<FileState> _pending = new();
    private FileObservation? _latest;
    private string? _discoveryAfter;
    private long _scan;
    private long _visit;

    internal CachedCodexSessionReader(
        string? homePath = null,
        long maxBytesPerScan = 8 * 1024 * 1024,
        int maxCachedFiles = 512,
        int maxLineBytes = 128 * 1024,
        int fileReadQuantum = 64 * 1024,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytesPerScan);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCachedFiles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLineBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileReadQuantum);
        _homePath = Path.GetFullPath(homePath ?? LocalStorage.GetCodexHome());
        _maxBytesPerScan = maxBytesPerScan;
        _maxCachedFiles = maxCachedFiles;
        _maxLineBytes = maxLineBytes;
        _fileReadQuantum = fileReadQuantum;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    internal long BytesReadLastScan { get; private set; }
    internal int CachedFileCount => _files.Count;
    internal bool LastScanComplete { get; private set; }

    internal CodexUsageSnapshot ReadLatest(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            BytesReadLastScan = 0;
            LastScanComplete = false;
            cancellationToken.ThrowIfCancellationRequested();
            _scan++;
            var now = _clock();
            var inventoryComplete = DiscoverFiles(cancellationToken);
            var buffer = new byte[Math.Min(16 * 1024, _fileReadQuantum)];
            var readFailed = false;
            while (_pending.First is { } next && BytesReadLastScan < _maxBytesPerScan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = next.Value;
                _pending.RemoveFirst();
                file.QueueNode = null;
                file.LastVisit = ++_visit;
                if (!ReadFile(file, buffer, now, cancellationToken))
                {
                    // Retry failed files on the next refresh, rather than spinning without consuming the budget.
                    readFailed = true;
                    continue;
                }
                QueueIfNeeded(file);
            }

            foreach (var file in _files.Values)
            {
                RememberLatest(file);
            }
            cancellationToken.ThrowIfCancellationRequested();
            LastScanComplete = inventoryComplete && !readFailed && _pending.Count == 0
                && _files.Values.All(file => file.PartialLength == 0 && !file.DiscardingLine);
            var latest = _latest?.Snapshot ?? throw new InvalidOperationException("No usable Codex usage measurement was found.");
            return latest with { Error = LastScanComplete ? null : "Local session scan incomplete." };
        }
    }

    private bool DiscoverFiles(CancellationToken cancellationToken)
    {
        var comparer = new DiscoveryOrder(_discoveryAfter);
        var candidates = new SortedSet<FileStamp>(comparer);
        var complete = true;
        var found = 0;
        var latestSeen = false;
        foreach (var directory in new[] { Path.Combine(_homePath, "sessions"), Path.Combine(_homePath, "archived_sessions") })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory)) continue;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            try
            {
                foreach (var path in Directory.EnumerateFiles(directory, "rollout-*.jsonl", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    found++;
                    if (_files.TryGetValue(path, out var file)) file.SeenAt = _scan;
                    if (StringComparer.OrdinalIgnoreCase.Equals(_latest?.Stamp.Path, path)) latestSeen = true;
                    FileStamp stamp;
                    try { stamp = ReadStamp(path); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        LocalStorage.TraceFailure("inspect fallback session", error);
                        complete = false;
                        continue;
                    }
                    if (_latest is { } latest && StringComparer.OrdinalIgnoreCase.Equals(latest.Stamp.Path, path))
                        _latest = NeedsReset(latest.Stamp, stamp, latest.Offset) ? null : latest with { Stamp = stamp };
                    if (file is not null)
                    {
                        Observe(file, stamp);
                        QueueIfNeeded(file);
                    }
                    else
                    {
                        candidates.Add(stamp);
                        if (candidates.Count > _maxCachedFiles) candidates.Remove(candidates.Max!);
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                LocalStorage.TraceFailure("enumerate fallback sessions", error);
                complete = false;
            }
        }

        if (complete)
        {
            foreach (var file in _files.Values.Where(file => file.SeenAt != _scan).ToArray()) Remove(file);
            if (!latestSeen) _latest = null;
        }
        var evictedPending = false;
        foreach (var stamp in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_files.Count == _maxCachedFiles)
            {
                var eligible = _files.Values.Where(file => file.AddedAt != _scan);
                var evicted = eligible.Where(file => file.QueueNode is null).MinBy(file => file.LastVisit);
                if (evicted is null && !evictedPending)
                {
                    evicted = eligible.Where(file => file.LastVisit > 0 && file.PartialLength == 0).MinBy(file => file.LastVisit);
                    evictedPending = evicted is not null;
                }
                if (evicted is null) break;
                RememberLatest(evicted);
                Remove(evicted);
            }
            var added = new FileState(stamp, _scan);
            _files.Add(stamp.Path, added);
            QueueIfNeeded(added);
            _discoveryAfter = stamp.Path;
        }
        // An overflow can require rereading evicted checkpoints; never label that bounded view a complete inventory.
        return complete && found <= _maxCachedFiles;
    }

    private bool ReadFile(FileState file, byte[] buffer, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            Observe(file, ReadStamp(file.Stamp.Path));
            using var stream = new FileStream(file.Stamp.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
            if (stream.Length < file.Stamp.Length)
            {
                Reset(file);
                file.Stamp = file.Stamp with { Length = stream.Length };
            }
            stream.Position = file.Offset;
            var remaining = Math.Min(Math.Min(_fileReadQuantum, _maxBytesPerScan - BytesReadLastScan), file.Stamp.Length - file.Offset);
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0) return false;
                ProcessBytes(file, buffer.AsSpan(0, read), now);
                file.Offset += read;
                BytesReadLastScan += read;
                remaining -= read;
                RememberLatest(file);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            LocalStorage.TraceFailure("read fallback session", error);
            return false;
        }
    }

    private void ProcessBytes(FileState file, ReadOnlySpan<byte> bytes, DateTimeOffset now)
    {
        while (!bytes.IsEmpty)
        {
            var newline = bytes.IndexOf((byte)'\n');
            var count = newline < 0 ? bytes.Length : newline;
            if (!file.DiscardingLine)
            {
                if (count > _maxLineBytes - file.PartialLength)
                {
                    file.ClearPartial();
                    file.DiscardingLine = true;
                }
                else
                {
                    var needed = file.PartialLength + count;
                    if (needed > file.Partial.Length)
                        Array.Resize(ref file.Partial, Math.Min(_maxLineBytes, Math.Max(needed, 1024)));
                    bytes[..count].CopyTo(file.Partial.AsSpan(file.PartialLength));
                    file.PartialLength = needed;
                }
            }
            if (newline < 0) break;
            if (!file.DiscardingLine) ParseLine(file, now);
            file.ClearPartial();
            file.DiscardingLine = false;
            bytes = bytes[(newline + 1)..];
        }
    }

    private static void ParseLine(FileState file, DateTimeOffset now)
    {
        var line = file.Partial.AsMemory(0, file.PartialLength);
        if (line.Span.IndexOf("\"rate_limits\""u8) < 0) return;
        if (line.Span.StartsWith("\uFEFF"u8)) line = line[3..];
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            var snapshot = LocalCodexSessionReader.ParseSnapshot(document.RootElement, now);
            if (snapshot is not null && (file.Latest is null || snapshot.UpdatedAt >= file.Latest.UpdatedAt))
                file.Latest = snapshot;
        }
        catch (JsonException)
        {
            // Malformed complete records are ignored; an unfinished record remains bounded until its newline arrives.
        }
    }

    private void Observe(FileState file, FileStamp stamp)
    {
        if (NeedsReset(file.Stamp, stamp, file.Offset)) Reset(file);
        file.Stamp = stamp;
    }

    private static bool NeedsReset(FileStamp previous, FileStamp current, long offset) => previous.CreatedAt != current.CreatedAt
        || current.Length < previous.Length || current.Length < offset
        || (current.Length == previous.Length && current.WrittenAt != previous.WrittenAt);

    private void Reset(FileState file)
    {
        if (StringComparer.OrdinalIgnoreCase.Equals(_latest?.Stamp.Path, file.Stamp.Path)) _latest = null;
        file.Offset = 0;
        file.Latest = null;
        file.ClearPartial();
        file.DiscardingLine = false;
    }

    private void RememberLatest(FileState file)
    {
        if (file.Latest is { } snapshot && (_latest is null || snapshot.UpdatedAt > _latest.Snapshot.UpdatedAt))
            _latest = new(file.Stamp, file.Offset, snapshot);
    }

    private void QueueIfNeeded(FileState file)
    {
        if (file.QueueNode is null && file.Offset < file.Stamp.Length) file.QueueNode = _pending.AddLast(file);
    }

    private void Remove(FileState file)
    {
        if (file.QueueNode is { } node) _pending.Remove(node);
        file.ClearPartial();
        _files.Remove(file.Stamp.Path);
    }

    private static FileStamp ReadStamp(string path)
    {
        var info = new FileInfo(path);
        return new(path, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
    }

    private sealed record FileStamp(string Path, long Length, DateTime WrittenAt, DateTime CreatedAt);
    private sealed record FileObservation(FileStamp Stamp, long Offset, CodexUsageSnapshot Snapshot);

    private sealed class FileState(FileStamp stamp, long scan)
    {
        internal FileStamp Stamp = stamp;
        internal long Offset;
        internal long SeenAt = scan;
        internal long AddedAt = scan;
        internal long LastVisit;
        internal byte[] Partial = [];
        internal int PartialLength;
        internal bool DiscardingLine;
        internal CodexUsageSnapshot? Latest;
        internal LinkedListNode<FileState>? QueueNode;

        internal void ClearPartial()
        {
            Partial.AsSpan(0, PartialLength).Clear();
            PartialLength = 0;
        }
    }

    private sealed class DiscoveryOrder(string? after) : IComparer<FileStamp>
    {
        public int Compare(FileStamp? left, FileStamp? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var leftAfter = after is null || StringComparer.OrdinalIgnoreCase.Compare(left.Path, after) > 0;
            var rightAfter = after is null || StringComparer.OrdinalIgnoreCase.Compare(right.Path, after) > 0;
            return leftAfter == rightAfter ? StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path) : leftAfter ? -1 : 1;
        }
    }
}

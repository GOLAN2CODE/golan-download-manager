using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Golan.Gdm.Core;

/// <summary>Thread-safe ring buffer of 1-second speed samples (default 600 samples = 10 minutes).</summary>
public sealed class SpeedHistory
{
    readonly object _gate = new();
    readonly long[] _buffer;
    int _head;
    int _count;

    public int Capacity => _buffer.Length;

    public SpeedHistory(int capacity = 600)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new long[capacity];
    }

    public int Count
    {
        get { lock (_gate) return _count; }
    }

    public void Add(long bytesPerSec)
    {
        long clamped = Math.Max(0, bytesPerSec);
        lock (_gate)
        {
            _buffer[_head] = clamped;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
        }
    }

    /// <summary>Returns a chronological copy of the samples (oldest first, newest last).</summary>
    public long[] Snapshot()
    {
        lock (_gate)
        {
            if (_count == 0) return [];
            var result = new long[_count];
            int start = _count < _buffer.Length ? 0 : _head;
            for (int i = 0; i < _count; i++)
                result[i] = _buffer[(start + i) % _buffer.Length];
            return result;
        }
    }

    public long Current
    {
        get
        {
            lock (_gate)
            {
                if (_count == 0) return 0;
                int lastIdx = (_head - 1 + _buffer.Length) % _buffer.Length;
                return _buffer[lastIdx];
            }
        }
    }

    public long Max
    {
        get
        {
            lock (_gate)
            {
                long max = 0;
                int start = _count < _buffer.Length ? 0 : _head;
                for (int i = 0; i < _count; i++)
                {
                    long v = _buffer[(start + i) % _buffer.Length];
                    if (v > max) max = v;
                }
                return max;
            }
        }
    }

    public double Average
    {
        get
        {
            lock (_gate)
            {
                if (_count == 0) return 0;
                long sum = 0;
                int start = _count < _buffer.Length ? 0 : _head;
                for (int i = 0; i < _count; i++)
                    sum += _buffer[(start + i) % _buffer.Length];
                return (double)sum / _count;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _head = 0;
            _count = 0;
        }
    }
}

public sealed class DailyStatsBucket
{
    public string Date { get; set; } = "";
    public long Bytes { get; set; }
    public int Files { get; set; }
    public long ActiveSeconds { get; set; }
    public Dictionary<string, long> ByCategory { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> ByHost { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum StatsRange
{
    Today,
    Last7Days,
    Last30Days,
    AllTime,
}

public sealed record StatsSummary(
    long TotalBytes,
    int TotalFiles,
    long ActiveSeconds,
    double AverageSpeedBytesPerSec,
    IReadOnlyList<(string Key, long Bytes)> ByCategory,
    IReadOnlyList<(string Host, long Bytes)> TopHosts,
    IReadOnlyList<(DateOnly Date, long Bytes, int Files)> DailySeries);

/// <summary>Persistent daily download statistics stored in stats.json (400-day retention, top 50 hosts per day).</summary>
public sealed class StatsStore : IDisposable
{
    public const int MaxRetentionDays = 400;
    public const int MaxHostsPerDay = 50;

    readonly object _gate = new();
    readonly SortedDictionary<string, DailyStatsBucket> _buckets = new(StringComparer.Ordinal);
    readonly Func<DateOnly> _todayProvider;
    readonly Timer? _flushTimer;
    bool _dirty;

    public string FilePath { get; }

    sealed class StatsFileModel
    {
        public int Version { get; set; } = 1;
        public List<DailyStatsBucket> Buckets { get; set; } = [];
    }

    public StatsStore(string? filePath = null, Func<DateOnly>? todayProvider = null, bool autoFlushTimer = false)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(AppPaths.DataDir, "stats.json")
            : Path.GetFullPath(filePath);
        _todayProvider = todayProvider ?? (() => DateOnly.FromDateTime(DateTime.Now));

        LoadFromDisk();

        if (autoFlushTimer)
        {
            _flushTimer = new Timer(_ =>
            {
                try { if (_dirty) Flush(); }
                catch (Exception ex) { Log.Write("stats timer flush failed: " + ex.Message); }
            }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }
    }

    void LoadFromDisk()
    {
        lock (_gate)
        {
            _buckets.Clear();
            if (!File.Exists(FilePath)) return;

            try
            {
                var raw = File.ReadAllText(FilePath, Encoding.UTF8);
                List<DailyStatsBucket>? list = null;
                if (raw.TrimStart().StartsWith('['))
                {
                    list = JsonSerializer.Deserialize<List<DailyStatsBucket>>(raw, Json.Options);
                }
                else
                {
                    var model = JsonSerializer.Deserialize<StatsFileModel>(raw, Json.Options);
                    list = model?.Buckets;
                }

                if (list == null)
                    throw new JsonException("Null stats payload");

                foreach (var b in list)
                {
                    if (b == null || !DateOnly.TryParseExact(b.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                        continue;

                    var norm = new DailyStatsBucket
                    {
                        Date = b.Date,
                        Bytes = Math.Max(0, b.Bytes),
                        Files = Math.Max(0, b.Files),
                        ActiveSeconds = Math.Max(0, b.ActiveSeconds),
                    };
                    if (b.ByCategory != null)
                    {
                        foreach (var (k, v) in b.ByCategory)
                            if (!string.IsNullOrWhiteSpace(k) && v > 0)
                                norm.ByCategory[k.Trim().ToLowerInvariant()] = v;
                    }
                    if (b.ByHost != null)
                    {
                        foreach (var (k, v) in b.ByHost)
                            if (!string.IsNullOrWhiteSpace(k) && v > 0)
                                norm.ByHost[k.Trim().ToLowerInvariant()] = v;
                    }
                    TrimHostsInBucket(norm);
                    _buckets[norm.Date] = norm;
                }

                TrimRetentionLocked();
            }
            catch (Exception ex)
            {
                Log.Write("stats.json corrupt, backing up to .bad: " + ex.Message);
                _buckets.Clear();
                try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); }
                catch (IOException) { /* best-effort corrupt stats.json backup */ }
            }
        }
    }

    DailyStatsBucket GetOrCreateBucketLocked(DateOnly? date)
    {
        var d = date ?? _todayProvider();
        var key = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!_buckets.TryGetValue(key, out var bucket))
        {
            bucket = new DailyStatsBucket { Date = key };
            _buckets[key] = bucket;
            if (_buckets.Count > MaxRetentionDays)
                TrimRetentionLocked();
        }
        return bucket;
    }

    static void TrimHostsInBucket(DailyStatsBucket bucket)
    {
        if (bucket.ByHost.Count <= MaxHostsPerDay) return;
        var top = bucket.ByHost
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(MaxHostsPerDay)
            .ToList();
        bucket.ByHost.Clear();
        foreach (var (k, v) in top)
            bucket.ByHost[k] = v;
    }

    void TrimRetentionLocked()
    {
        while (_buckets.Count > MaxRetentionDays)
        {
            var oldestKey = _buckets.Keys.First();
            _buckets.Remove(oldestKey);
        }
    }

    public void Record(long bytes, string? host = null, string? category = null, DateOnly? date = null)
    {
        if (bytes <= 0) return;
        var normHost = NormalizeHost(host);
        var normCat = string.IsNullOrWhiteSpace(category) ? "general" : category.Trim().ToLowerInvariant();

        lock (_gate)
        {
            var b = GetOrCreateBucketLocked(date);
            b.Bytes += bytes;
            b.ByCategory[normCat] = b.ByCategory.GetValueOrDefault(normCat) + bytes;
            if (normHost.Length > 0)
            {
                b.ByHost[normHost] = b.ByHost.GetValueOrDefault(normHost) + bytes;
                if (b.ByHost.Count > MaxHostsPerDay * 4)
                    TrimHostsInBucket(b);
            }
            _dirty = true;
        }
    }

    public void RecordFileCompleted(string? host = null, string? category = null, DateOnly? date = null)
    {
        lock (_gate)
        {
            var b = GetOrCreateBucketLocked(date);
            b.Files++;
            _dirty = true;
        }
    }

    public void RecordActiveSecond(DateOnly? date = null, int seconds = 1)
    {
        if (seconds <= 0) return;
        lock (_gate)
        {
            var b = GetOrCreateBucketLocked(date);
            b.ActiveSeconds += seconds;
            _dirty = true;
        }
    }

    static string NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return "";
        var h = host.Trim();
        if (h.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(h, UriKind.Absolute, out var u))
            h = u.Host;
        return h.ToLowerInvariant();
    }

    public IReadOnlyList<DailyStatsBucket> GetBuckets()
    {
        lock (_gate)
        {
            TrimRetentionLocked();
            var list = new List<DailyStatsBucket>(_buckets.Count);
            foreach (var b in _buckets.Values)
            {
                TrimHostsInBucket(b);
                list.Add(new DailyStatsBucket
                {
                    Date = b.Date,
                    Bytes = b.Bytes,
                    Files = b.Files,
                    ActiveSeconds = b.ActiveSeconds,
                    ByCategory = new Dictionary<string, long>(b.ByCategory, StringComparer.OrdinalIgnoreCase),
                    ByHost = new Dictionary<string, long>(b.ByHost, StringComparer.OrdinalIgnoreCase),
                });
            }
            return list;
        }
    }

    public StatsSummary Query(StatsRange range, DateOnly? today = null)
    {
        var anchor = today ?? _todayProvider();
        DateOnly minDate = range switch
        {
            StatsRange.Today => anchor,
            StatsRange.Last7Days => anchor.AddDays(-6),
            StatsRange.Last30Days => anchor.AddDays(-29),
            _ => DateOnly.MinValue,
        };

        long totalBytes = 0;
        int totalFiles = 0;
        long activeSec = 0;
        var catTotals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var hostTotals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var series = new List<(DateOnly Date, long Bytes, int Files)>();

        lock (_gate)
        {
            TrimRetentionLocked();
            foreach (var b in _buckets.Values)
            {
                TrimHostsInBucket(b);
                if (!DateOnly.TryParseExact(b.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    continue;
                if (d < minDate || d > anchor) continue;

                totalBytes += b.Bytes;
                totalFiles += b.Files;
                activeSec += b.ActiveSeconds;
                series.Add((d, b.Bytes, b.Files));

                foreach (var (k, v) in b.ByCategory)
                    catTotals[k] = catTotals.GetValueOrDefault(k) + v;
                foreach (var (k, v) in b.ByHost)
                    hostTotals[k] = hostTotals.GetValueOrDefault(k) + v;
            }
        }

        double avgSpeed = activeSec > 0 ? (double)totalBytes / activeSec : 0;
        var byCatList = catTotals
            .OrderByDescending(kv => kv.Value)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
        var topHostsList = hostTotals
            .OrderByDescending(kv => kv.Value)
            .Take(10)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();

        return new StatsSummary(totalBytes, totalFiles, activeSec, avgSpeed, byCatList, topHostsList, series);
    }

    public void Flush()
    {
        lock (_gate)
        {
            TrimRetentionLocked();
            foreach (var b in _buckets.Values)
                TrimHostsInBucket(b);

            var model = new StatsFileModel
            {
                Version = 1,
                Buckets = _buckets.Values.ToList(),
            };
            var json = JsonSerializer.Serialize(model, Json.Options);

            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, FilePath, overwrite: true);
            _dirty = false;
        }
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
        try { if (_dirty) Flush(); }
        catch (Exception ex) { Log.Write("stats dispose flush failed: " + ex.Message); }
    }
}

using System.Collections.ObjectModel;
using System.Text;

namespace ChartScene.Samples;

public readonly record struct ExampleSearchStatistics(long Executions, long ExaminedEntries, long CacheHits, int CachedQueries);

/// <summary>
/// Searches immutable example metadata, description and source code without repeatedly building
/// normalized strings during typing. Identical normalized queries return the same immutable result.
/// Cache retention is bounded; no chart models or GPU resources are constructed by a search.
/// </summary>
public sealed class ExampleSearchIndex
{
    private sealed record Entry(ChartExample Example, string Searchable);
    private readonly Entry[] _entries;
    private readonly ReadOnlyCollection<ChartExample> _all;
    private readonly Dictionary<string, ReadOnlyCollection<ChartExample>> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();
    private long _executions, _examined, _hits;
    public IReadOnlyList<ChartExample> All => _all;
    public ExampleSearchStatistics Statistics { get { lock (_gate) return new(_executions, _examined, _hits, _cache.Count); } }
    public ExampleSearchIndex(IEnumerable<ChartExample> examples)
    {
        ArgumentNullException.ThrowIfNull(examples);
        var values = examples.ToArray();
        if (values.Any(e => e is null || string.IsNullOrWhiteSpace(e.Id)) || values.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new ArgumentException("Examples require unique nonempty identifiers.", nameof(examples));
        _all = Array.AsReadOnly(values);
        _entries = values.Select(e => new Entry(e, string.Concat(e.Id, " ", e.Title, " ", e.Category, " ", e.Description, " ", e.Source).ToUpperInvariant())).ToArray();
    }
    public IReadOnlyList<ChartExample> Search(string? query)
    {
        string key = Normalize(query);
        lock (_gate)
        {
            if (key.Length == 0) { _hits++; return _all; }
            if (_cache.TryGetValue(key, out var cached)) { _hits++; return cached; }
            string[] terms = key.Split(' ');
            var matches = new List<ChartExample>();
            foreach (var entry in _entries)
            {
                bool match = true;
                foreach (string term in terms) if (!entry.Searchable.Contains(term, StringComparison.Ordinal)) { match = false; break; }
                if (match) matches.Add(entry.Example);
            }
            _executions++; _examined += _entries.Length;
            var result = matches.AsReadOnly();
            if (_cache.Count == 16) _cache.Remove(_order.Dequeue());
            _cache.Add(key, result); _order.Enqueue(key); return result;
        }
    }
    private static string Normalize(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "";
        if (query.Length > 4096) throw new ArgumentOutOfRangeException(nameof(query), "Example search is limited to 4096 characters.");
        // Culture-invariant whole-token matching; quoted/expression searches are not implied.
        return string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }
}

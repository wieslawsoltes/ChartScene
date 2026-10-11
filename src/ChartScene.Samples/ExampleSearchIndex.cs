using System.Collections.ObjectModel;

namespace ChartScene.Samples;

public readonly record struct ExampleSearchStatistics(long Executions, long ExaminedEntries, long CacheHits, int CachedQueries);

/// <summary>
/// Immutable metadata/source search. Retains at most sixteen normalized result sets and one
/// last-input string. Repeated equal inputs bypass normalization, scanning and allocations.
/// Searching never constructs a chart model or graphics resource.
/// </summary>
public sealed class ExampleSearchIndex
{
    private sealed record Entry(ChartExample Example, string Searchable);
    private readonly Entry[] _entries;
    private readonly ReadOnlyCollection<ChartExample> _all;
    private readonly Dictionary<string, ReadOnlyCollection<ChartExample>> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();
    private string? _lastInput;
    private ReadOnlyCollection<ChartExample>? _lastResult;
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
        // Validate before scanning whitespace or touching cache state. Whitespace-only input
        // obeys the same bound, and rejected input leaves the last valid generation intact.
        if (query is { Length: > 4096 }) throw new ArgumentOutOfRangeException(nameof(query), "Example search is limited to 4096 characters.");
        lock (_gate)
        {
            if (_lastResult is not null && string.Equals(query, _lastInput, StringComparison.Ordinal))
            { _hits++; return _lastResult; }
            string key = Normalize(query);
            ReadOnlyCollection<ChartExample> result;
            if (key.Length == 0) { _hits++; result = _all; }
            else if (_cache.TryGetValue(key, out var cached)) { _hits++; result = cached; }
            else
            {
                string[] terms = key.Split(' ');
                var matches = new List<ChartExample>();
                foreach (var entry in _entries)
                {
                    bool match = true;
                    foreach (string term in terms) if (!entry.Searchable.Contains(term, StringComparison.Ordinal)) { match = false; break; }
                    if (match) matches.Add(entry.Example);
                }
                _executions++; _examined += _entries.Length;
                result = matches.AsReadOnly();
                if (_cache.Count == 16) _cache.Remove(_order.Dequeue());
                _cache.Add(key, result); _order.Enqueue(key);
            }
            _lastInput = query; _lastResult = result;
            return result;
        }
    }
    private static string Normalize(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "";
        // Culture-invariant all-term substring matching; no quoted/expression syntax implied.
        return string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }
}

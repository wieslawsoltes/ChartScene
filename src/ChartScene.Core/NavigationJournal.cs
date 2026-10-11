namespace ChartScene.Core;

/// <summary>Bounded, UI-independent back/forward history. Owners serialize UI writes to one thread.</summary>
public sealed class NavigationJournal<T> where T : notnull
{
    private readonly List<T> _entries = [];
    private readonly IEqualityComparer<T> _comparer;
    private int _position = -1;
    public int Capacity { get; }
    public int Count => _entries.Count;
    public bool HasCurrent => _position >= 0;
    public T Current => HasCurrent ? _entries[_position] : throw new InvalidOperationException("The journal is empty.");
    public bool CanGoBack => _position > 0;
    public bool CanGoForward => _position + 1 < _entries.Count;
    public event EventHandler? Changed;
    public NavigationJournal(int capacity = 128, IEqualityComparer<T>? comparer = null)
    {
        if (capacity < 2 || capacity > 10000) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity; _comparer = comparer ?? EqualityComparer<T>.Default;
    }
    public bool Navigate(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (HasCurrent && _comparer.Equals(Current, value)) return false;
        if (CanGoForward) _entries.RemoveRange(_position + 1, _entries.Count - _position - 1);
        _entries.Add(value);
        if (_entries.Count > Capacity) _entries.RemoveAt(0);
        _position = _entries.Count - 1; Changed?.Invoke(this, EventArgs.Empty); return true;
    }
    public bool GoBack()
    { if (!CanGoBack) return false; _position--; Changed?.Invoke(this, EventArgs.Empty); return true; }
    public bool GoForward()
    { if (!CanGoForward) return false; _position++; Changed?.Invoke(this, EventArgs.Empty); return true; }
    public IReadOnlyList<T> Snapshot() => Array.AsReadOnly(_entries.ToArray());
    public void Clear()
    { if (_entries.Count == 0) return; _entries.Clear(); _position = -1; Changed?.Invoke(this, EventArgs.Empty); }
}

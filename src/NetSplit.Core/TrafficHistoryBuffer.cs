namespace NetSplit.Core;

/// <summary>
/// Thread-safe circular buffer that retains up to <see cref="Capacity"/> traffic samples.
/// At a 5-second poll interval this covers 5 minutes of history.
/// </summary>
public sealed class TrafficHistoryBuffer
{
    public const int Capacity = 60;

    private readonly TrafficPoint[] _buffer = new TrafficPoint[Capacity];
    private readonly object _lock = new();
    private int _head;  // next write index
    private int _count; // valid entries (0..Capacity)

    public void Add(TrafficPoint point)
    {
        lock (_lock)
        {
            _buffer[_head] = point;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity)
            {
                _count++;
            }
        }
    }

    /// <summary>Returns a snapshot ordered oldest-first.</summary>
    public IReadOnlyList<TrafficPoint> Snapshot()
    {
        lock (_lock)
        {
            if (_count == 0)
            {
                return [];
            }

            var result = new TrafficPoint[_count];
            var start = (_head - _count + Capacity) % Capacity;
            for (var i = 0; i < _count; i++)
            {
                result[i] = _buffer[(start + i) % Capacity];
            }

            return result;
        }
    }
}

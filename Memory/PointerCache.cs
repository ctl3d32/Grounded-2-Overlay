using System.Diagnostics;

namespace G2Overlay.Memory;

/// <summary>
/// Cache de ponteiros com TTL. Usado para a cadeia estática
/// (GWorld → GameInstance → LocalPlayer → PlayerController → CameraManager), que muda raramente.
/// Reduz o número de ReadProcessMemory por frame.
/// </summary>
public sealed class PointerCache
{
    private readonly struct Entry
    {
        public readonly ulong Value;
        public readonly long Expires;

        public Entry(ulong value, long expires)
        {
            Value = value;
            Expires = expires;
        }
    }

    private readonly Dictionary<ulong, Entry> _entries = new();
    private readonly ProcessMemory _mem;

    public long Hits { get; private set; }
    public long Misses { get; private set; }

    public PointerCache(ProcessMemory mem) => _mem = mem;

    /// <summary>Lê *address com cache de ttlMs milissegundos.</summary>
    public ulong ReadPtr(ulong address, int ttlMs)
    {
        long now = Stopwatch.GetTimestamp();
        if (_entries.TryGetValue(address, out var e) && e.Expires > now)
        {
            Hits++;
            return e.Value;
        }

        Misses++;
        ulong v = _mem.ReadPtr(address);
        _entries[address] = new Entry(v, now + ttlMs * Stopwatch.Frequency / 1000);
        if (_entries.Count > 4096) Prune(now);
        return v;
    }

    public void Invalidate(ulong address) => _entries.Remove(address);

    public void Clear() => _entries.Clear();

    private void Prune(long now)
    {
        var dead = new List<ulong>();
        foreach (var kv in _entries)
            if (kv.Value.Expires <= now) dead.Add(kv.Key);
        foreach (var k in dead) _entries.Remove(k);
    }
}

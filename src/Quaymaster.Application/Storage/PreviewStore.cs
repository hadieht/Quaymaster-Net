using System.Collections.Concurrent;

namespace Quaymaster.Application.Storage;

/// <summary>
/// Mandatory previews (cleanup): an execute call only accepts the id of a preview created by the same admin in the last 10
/// minutes. Previews live in memory only - a restart invalidates them, which is the safe direction.
/// </summary>
public sealed class PreviewStore<T>(TimeProvider time)
    where T : class
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, (T Value, string Actor, DateTimeOffset ExpiresAt)> _items = new(StringComparer.Ordinal);

    public DateTimeOffset Put(string id, T value, string actor)
    {
        var now = time.GetUtcNow();
        foreach (var (key, entry) in _items)
        {
            if (entry.ExpiresAt < now)
            {
                _items.TryRemove(key, out _);
            }
        }

        var expires = now + Ttl;
        _items[id] = (value, actor, expires);
        return expires;
    }

    /// <summary>The stored value, or null when unknown or expired.</summary>
    public (T Value, string Actor)? Get(string id) =>
        _items.TryGetValue(id, out var e) && e.ExpiresAt >= time.GetUtcNow() ? (e.Value, e.Actor) : null;

    public void Remove(string id) => _items.TryRemove(id, out _);
}

using System;
using System.Collections.Generic;

namespace ProsperoThemes.App.Library;

/// <summary>
/// The store downloads the user asked for while the app was busy, in the order they were asked for. A download runs at a time:
/// <see cref="TryDequeue"/> takes the next one and marks it as the active one until <see cref="Finish"/>. A store id is in the
/// queue or active once, so pressing Download on a card twice does not fetch the pack twice. It holds the items themselves,
/// not their place in a list, so a reload of the store list (another sort, chip or search) does not touch it.
/// Used on the frame thread only; the renderer reads <see cref="PendingIds"/> and <see cref="ActiveId"/> for the cards' badges.
/// </summary>
/// <typeparam name="T">What is downloaded (a store theme).</typeparam>
public sealed class DownloadQueue<T>
{
    private readonly Func<T, string> _idOf;
    private readonly Queue<(T Item, bool Then)> _waiting = new();
    // The ids of the waiting items. One set for the whole life of the queue (never replaced), so the renderer can hold it
    // and a steady frame asks it without allocating.
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);

    /// <param name="idOf">The store id of an item: what makes two requests the same download.</param>
    public DownloadQueue(Func<T, string> idOf) => _idOf = idOf;

    /// <summary>How many downloads wait (the active one is not counted).</summary>
    public int Count => _waiting.Count;

    /// <summary>The ids that wait. The set is live: it changes as items come and go, and is the same object for good.</summary>
    public IReadOnlySet<string> PendingIds => _pending;

    /// <summary>The id of the download that runs now (taken by <see cref="TryDequeue"/>, until <see cref="Finish"/>); null when none.</summary>
    public string? ActiveId { get; private set; }

    /// <summary>Whether the id waits in the queue.</summary>
    public bool IsPending(string id) => _pending.Contains(id);

    /// <summary>Whether the id waits in the queue or is the download that runs now.</summary>
    public bool Contains(string id) => _pending.Contains(id) || ActiveId == id;

    /// <summary>
    /// Adds a download at the end. <paramref name="then"/> is the caller's flag for what follows the download (the theme is
    /// applied afterwards); it comes back from <see cref="TryDequeue"/>. Returns false, and changes nothing, when the id
    /// waits already or is the one downloading.
    /// </summary>
    public bool Enqueue(T item, bool then = false)
    {
        string id = _idOf(item);
        if (Contains(id)) return false;
        _waiting.Enqueue((item, then));
        _pending.Add(id);
        return true;
    }

    /// <summary>
    /// Takes the oldest waiting download and makes it the active one (<see cref="ActiveId"/>). Returns false when none waits.
    /// Whatever was active before is not active any more, so a caller that forgot <see cref="Finish"/> cannot hold the queue.
    /// </summary>
    public bool TryDequeue(out T item, out bool then)
    {
        if (!_waiting.TryDequeue(out (T Item, bool Then) next))
        {
            item = default!;
            then = false;
            return false;
        }
        item = next.Item;
        then = next.Then;
        string id = _idOf(item);
        _pending.Remove(id);
        ActiveId = id;
        return true;
    }

    /// <summary>Marks the active download as done (it succeeded, failed or never started); the next one can be taken.</summary>
    public void Finish() => ActiveId = null;

    /// <summary>Marks an id as the active one for a download that did not come through the queue (one started on an idle app).</summary>
    public void Begin(string id) => ActiveId = id;

    /// <summary>Drops every waiting download (the store address is gone, so none can run).</summary>
    public void Clear()
    {
        _waiting.Clear();
        _pending.Clear();
    }
}

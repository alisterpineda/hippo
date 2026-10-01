namespace Hippo.Indexing;

/// <summary>Collects items and hands them to <c>write</c> as soon as <c>size</c> of them are waiting, so what waits to be
/// written never outgrows one batch, however many items come. <see cref="Flush"/> writes what is left. <c>write</c> must
/// not keep the list it is given, which is reused for the next batch.</summary>
internal sealed class Batches<T>(int size, Action<List<T>> write)
{
    private readonly List<T> _pending = new(size);

    /// <summary>How many items wait to be written.</summary>
    public int Pending => _pending.Count;

    public void Add(T item)
    {
        _pending.Add(item);
        if (_pending.Count >= size)
        {
            Flush();
        }
    }

    public void Flush()
    {
        if (_pending.Count == 0)
        {
            return;
        }
        write(_pending);
        _pending.Clear();
    }
}

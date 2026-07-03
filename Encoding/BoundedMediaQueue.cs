using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Recorder.Encoding;

internal sealed class BoundedMediaQueue<T> : IDisposable
{
    private readonly BlockingCollection<T> _queue;

    public BoundedMediaQueue(int capacity)
    {
        _queue = new BlockingCollection<T>(Math.Max(1, capacity));
    }

    public int Count => _queue.Count;

    public IEnumerable<T> GetConsumingEnumerable()
        => _queue.GetConsumingEnumerable();

    public bool TryEnqueueDropOldest(T item, Action<T> disposeDropped, out int droppedCount)
    {
        droppedCount = 0;
        while (true)
        {
            try
            {
                if (_queue.TryAdd(item, 0))
                    return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            if (!_queue.TryTake(out T? dropped))
                return false;

            droppedCount++;
            disposeDropped(dropped);
        }
    }

    public bool TryEnqueueDropIncoming(T item)
    {
        try
        {
            return _queue.TryAdd(item, 0);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public bool TryTake(out T item)
        => _queue.TryTake(out item!);

    public void CompleteAdding()
    {
        try { _queue.CompleteAdding(); } catch { }
    }

    public int Drain(Action<T> disposeItem)
    {
        int drained = 0;
        while (_queue.TryTake(out T? item))
        {
            disposeItem(item);
            drained++;
        }

        return drained;
    }

    public void Dispose()
        => _queue.Dispose();
}

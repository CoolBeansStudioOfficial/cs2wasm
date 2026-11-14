#pragma warning disable CS0436
// Concurrent collections (section 4.5): a lock per collection around the plain ones. Callbacks (GetOrAdd's factory,
// AddOrUpdate's functions) run outside the lock, as in .NET, so they may run more than once under contention.
// Enumeration is over a snapshot. ConcurrentDictionary enumerates in insertion order, not .NET's bucket order.
using System.Collections;
using System.Collections.Generic;

namespace System.Collections.Concurrent
{
    public class ConcurrentQueue<T> : IReadOnlyCollection<T>
    {
        readonly Queue<T> _q = new Queue<T>();
        readonly object _gate = new object();
        public ConcurrentQueue() { }
        public ConcurrentQueue(IEnumerable<T> collection) { foreach (var x in collection) _q.Enqueue(x); }
        public void Enqueue(T item) { lock (_gate) _q.Enqueue(item); }
        public bool TryDequeue(out T result)
        {
            lock (_gate)
            {
                if (_q.Count == 0) { result = default; return false; }
                result = _q.Dequeue();
                return true;
            }
        }
        public bool TryPeek(out T result)
        {
            lock (_gate)
            {
                if (_q.Count == 0) { result = default; return false; }
                result = _q.Peek();
                return true;
            }
        }
        public int Count { get { lock (_gate) return _q.Count; } }
        public bool IsEmpty => Count == 0;
        public void Clear() { lock (_gate) _q.Clear(); }
        public T[] ToArray() { lock (_gate) return _q.ToArray(); }
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)ToArray()).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public class ConcurrentStack<T> : IReadOnlyCollection<T>
    {
        readonly Stack<T> _s = new Stack<T>();
        readonly object _gate = new object();
        public void Push(T item) { lock (_gate) _s.Push(item); }
        public bool TryPop(out T result)
        {
            lock (_gate)
            {
                if (_s.Count == 0) { result = default; return false; }
                result = _s.Pop();
                return true;
            }
        }
        public bool TryPeek(out T result)
        {
            lock (_gate)
            {
                if (_s.Count == 0) { result = default; return false; }
                result = _s.Peek();
                return true;
            }
        }
        public int Count { get { lock (_gate) return _s.Count; } }
        public bool IsEmpty => Count == 0;
        public void Clear() { lock (_gate) _s.Clear(); }
        public T[] ToArray() { lock (_gate) return _s.ToArray(); }
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)ToArray()).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Unordered bag: on one thread, TryTake returns the most recently added item, as .NET's does.</summary>
    public class ConcurrentBag<T> : IReadOnlyCollection<T>
    {
        readonly List<T> _items = new List<T>();
        readonly object _gate = new object();
        public ConcurrentBag() { }
        public ConcurrentBag(IEnumerable<T> collection) { _items.AddRange(collection); }
        public void Add(T item) { lock (_gate) _items.Add(item); }
        public bool TryTake(out T result)
        {
            lock (_gate)
            {
                if (_items.Count == 0) { result = default; return false; }
                result = _items[_items.Count - 1];
                _items.RemoveAt(_items.Count - 1);
                return true;
            }
        }
        public bool TryPeek(out T result)
        {
            lock (_gate)
            {
                if (_items.Count == 0) { result = default; return false; }
                result = _items[_items.Count - 1];
                return true;
            }
        }
        public int Count { get { lock (_gate) return _items.Count; } }
        public bool IsEmpty => Count == 0;
        public void Clear() { lock (_gate) _items.Clear(); }
        public T[] ToArray()
        {
            lock (_gate)
            {
                var r = new T[_items.Count];
                for (int i = 0; i < r.Length; i++) r[i] = _items[_items.Count - 1 - i];
                return r;
            }
        }
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)ToArray()).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public class ConcurrentDictionary<TKey, TValue> : IReadOnlyCollection<KeyValuePair<TKey, TValue>>
    {
        readonly Dictionary<TKey, TValue> _d;
        readonly object _gate = new object();
        public ConcurrentDictionary() { _d = new Dictionary<TKey, TValue>(); }
        public ConcurrentDictionary(IEqualityComparer<TKey> comparer) { _d = new Dictionary<TKey, TValue>(comparer); }
        public ConcurrentDictionary(int concurrencyLevel, int capacity) { _d = new Dictionary<TKey, TValue>(); }
        public ConcurrentDictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection) : this() { foreach (var kv in collection) _d[kv.Key] = kv.Value; }

        public TValue this[TKey key]
        {
            get
            {
                lock (_gate)
                {
                    if (_d.TryGetValue(key, out var v)) return v;
                }
                throw new KeyNotFoundException("The given key '" + key + "' was not present in the dictionary.");
            }
            set { if (key == null) throw new ArgumentNullException("key"); lock (_gate) _d[key] = value; }
        }
        public int Count { get { lock (_gate) return _d.Count; } }
        public bool IsEmpty => Count == 0;
        public bool ContainsKey(TKey key) { lock (_gate) return _d.ContainsKey(key); }
        public bool TryGetValue(TKey key, out TValue value) { lock (_gate) return _d.TryGetValue(key, out value); }
        public bool TryAdd(TKey key, TValue value) { if (key == null) throw new ArgumentNullException("key"); lock (_gate) return _d.TryAdd(key, value); }
        public bool TryRemove(TKey key, out TValue value) { lock (_gate) return _d.Remove(key, out value); }
        public bool TryUpdate(TKey key, TValue newValue, TValue comparisonValue)
        {
            lock (_gate)
            {
                if (!_d.TryGetValue(key, out var cur) || !EqualityComparer<TValue>.Default.Equals(cur, comparisonValue)) return false;
                _d[key] = newValue;
                return true;
            }
        }
        public TValue GetOrAdd(TKey key, TValue value)
        {
            if (key == null) throw new ArgumentNullException("key");
            lock (_gate)
            {
                if (_d.TryGetValue(key, out var v)) return v;
                _d[key] = value;
                return value;
            }
        }
        public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
        {
            if (key == null) throw new ArgumentNullException("key");
            lock (_gate) { if (_d.TryGetValue(key, out var v)) return v; }
            var made = valueFactory(key);
            return GetOrAdd(key, made);
        }
        public TValue AddOrUpdate(TKey key, TValue addValue, Func<TKey, TValue, TValue> updateValueFactory)
        {
            if (key == null) throw new ArgumentNullException("key");
            while (true)
            {
                TValue cur;
                lock (_gate)
                {
                    if (!_d.TryGetValue(key, out cur)) { _d[key] = addValue; return addValue; }
                }
                var next = updateValueFactory(key, cur);
                if (TryUpdate(key, next, cur)) return next;
            }
        }
        public TValue AddOrUpdate(TKey key, Func<TKey, TValue> addValueFactory, Func<TKey, TValue, TValue> updateValueFactory)
        {
            if (key == null) throw new ArgumentNullException("key");
            while (true)
            {
                TValue cur;
                bool has;
                lock (_gate) has = _d.TryGetValue(key, out cur);
                if (!has)
                {
                    var add = addValueFactory(key);
                    if (TryAdd(key, add)) return add;
                    continue;
                }
                var next = updateValueFactory(key, cur);
                if (TryUpdate(key, next, cur)) return next;
            }
        }
        public void Clear() { lock (_gate) _d.Clear(); }
        public KeyValuePair<TKey, TValue>[] ToArray()
        {
            lock (_gate)
            {
                var r = new KeyValuePair<TKey, TValue>[_d.Count];
                int i = 0;
                foreach (var kv in _d) r[i++] = kv;
                return r;
            }
        }
        public ICollection<TKey> Keys { get { var l = new List<TKey>(); foreach (var kv in ToArray()) l.Add(kv.Key); return l; } }
        public ICollection<TValue> Values { get { var l = new List<TValue>(); foreach (var kv in ToArray()) l.Add(kv.Value); return l; } }
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => ((IEnumerable<KeyValuePair<TKey, TValue>>)ToArray()).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

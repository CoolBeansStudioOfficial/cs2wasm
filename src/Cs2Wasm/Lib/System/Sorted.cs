#pragma warning disable CS0436
// Sorted collections over sorted lists with binary search; enumeration in key order, as .NET's.
using System.Collections;

namespace System.Collections.Generic
{
    public class SortedList<TKey, TValue> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
    {
        readonly List<TKey> _keys = new List<TKey>();
        readonly List<TValue> _values = new List<TValue>();
        readonly IComparer<TKey> _cmp;
        public SortedList() : this((IComparer<TKey>)null) { }
        public SortedList(IComparer<TKey> comparer) { _cmp = comparer ?? Comparer<TKey>.Default; }
        public SortedList(int capacity) : this() { }
        public SortedList(IDictionary<TKey, TValue> d) : this() { foreach (var kv in d) Add(kv.Key, kv.Value); }
        public IComparer<TKey> Comparer => _cmp;
        public int Count => _keys.Count;
        public bool IsReadOnly => false;
        internal int Find(TKey key)
        {
            if (key == null) throw new ArgumentNullException("key");
            int lo = 0, hi = _keys.Count - 1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                int c = _cmp.Compare(_keys[mid], key);
                if (c == 0) return mid;
                if (c < 0) lo = mid + 1; else hi = mid - 1;
            }
            return ~lo;
        }
        public TValue this[TKey key]
        {
            get { int i = Find(key); if (i < 0) throw new KeyNotFoundException("The given key '" + key + "' was not present in the dictionary."); return _values[i]; }
            set { int i = Find(key); if (i >= 0) _values[i] = value; else { _keys.Insert(~i, key); _values.Insert(~i, value); } }
        }
        public void Add(TKey key, TValue value)
        {
            int i = Find(key);
            if (i >= 0) throw new ArgumentException("An item with the same key has already been added. Key: " + key);
            _keys.Insert(~i, key); _values.Insert(~i, value);
        }
        public bool TryAdd(TKey key, TValue value) { int i = Find(key); if (i >= 0) return false; _keys.Insert(~i, key); _values.Insert(~i, value); return true; }
        public bool ContainsKey(TKey key) => Find(key) >= 0;
        public bool ContainsValue(TValue value) => _values.Contains(value);
        public bool TryGetValue(TKey key, out TValue value) { int i = Find(key); if (i >= 0) { value = _values[i]; return true; } value = default; return false; }
        public bool Remove(TKey key) { int i = Find(key); if (i < 0) return false; _keys.RemoveAt(i); _values.RemoveAt(i); return true; }
        public void RemoveAt(int index) { _keys.RemoveAt(index); _values.RemoveAt(index); }
        public int IndexOfKey(TKey key) { int i = Find(key); return i < 0 ? -1 : i; }
        public TKey GetKeyAtIndex(int index) => _keys[index];
        public TValue GetValueAtIndex(int index) => _values[index];
        public void Clear() { _keys.Clear(); _values.Clear(); }
        public IList<TKey> Keys => _keys;
        public IList<TValue> Values => _values;
        public void Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);
        public bool Contains(KeyValuePair<TKey, TValue> item) => TryGetValue(item.Key, out var v) && EqualityComparer<TValue>.Default.Equals(v, item.Value);
        public bool Remove(KeyValuePair<TKey, TValue> item) => Contains(item) && Remove(item.Key);
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int index) { for (int i = 0; i < _keys.Count; i++) array[index + i] = new KeyValuePair<TKey, TValue>(_keys[i], _values[i]); }
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() { for (int i = 0; i < _keys.Count; i++) yield return new KeyValuePair<TKey, TValue>(_keys[i], _values[i]); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public class SortedDictionary<TKey, TValue> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
    {
        readonly SortedList<TKey, TValue> _list;
        public SortedDictionary() { _list = new SortedList<TKey, TValue>(); }
        public SortedDictionary(IComparer<TKey> comparer) { _list = new SortedList<TKey, TValue>(comparer); }
        public SortedDictionary(IDictionary<TKey, TValue> d) : this() { foreach (var kv in d) Add(kv.Key, kv.Value); }
        public IComparer<TKey> Comparer => _list.Comparer;
        public int Count => _list.Count;
        public bool IsReadOnly => false;
        public TValue this[TKey key] { get => _list[key]; set => _list[key] = value; }
        public void Add(TKey key, TValue value) => _list.Add(key, value);
        public bool TryAdd(TKey key, TValue value) => _list.TryAdd(key, value);
        public bool ContainsKey(TKey key) => _list.ContainsKey(key);
        public bool ContainsValue(TValue value) => _list.ContainsValue(value);
        public bool TryGetValue(TKey key, out TValue value) => _list.TryGetValue(key, out value);
        public bool Remove(TKey key) => _list.Remove(key);
        public void Clear() => _list.Clear();
        public KeyCollection Keys => new KeyCollection(this);
        public ValueCollection Values => new ValueCollection(this);
        public void Add(KeyValuePair<TKey, TValue> item) => _list.Add(item);
        public bool Contains(KeyValuePair<TKey, TValue> item) => _list.Contains(item);
        public bool Remove(KeyValuePair<TKey, TValue> item) => _list.Remove(item);
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int index) => _list.CopyTo(array, index);
        public Enumerator GetEnumerator() => new Enumerator(this);
        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
        {
            readonly SortedDictionary<TKey, TValue> _d; int _i;
            internal Enumerator(SortedDictionary<TKey, TValue> d) { _d = d; _i = -1; }
            public bool MoveNext() => ++_i < _d._list.Count;
            public KeyValuePair<TKey, TValue> Current => new KeyValuePair<TKey, TValue>(_d._list.GetKeyAtIndex(_i), _d._list.GetValueAtIndex(_i));
            object IEnumerator.Current => Current;
            public void Reset() { _i = -1; }
            public void Dispose() { }
        }

        public sealed class KeyCollection : IReadOnlyCollection<TKey>
        {
            readonly SortedDictionary<TKey, TValue> _d;
            internal KeyCollection(SortedDictionary<TKey, TValue> d) { _d = d; }
            public int Count => _d.Count;
            public IEnumerator<TKey> GetEnumerator() => _d._list.Keys.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
        public sealed class ValueCollection : IReadOnlyCollection<TValue>
        {
            readonly SortedDictionary<TKey, TValue> _d;
            internal ValueCollection(SortedDictionary<TKey, TValue> d) { _d = d; }
            public int Count => _d.Count;
            public IEnumerator<TValue> GetEnumerator() => _d._list.Values.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }

    public class SortedSet<T> : IReadOnlyCollection<T>, ICollection<T>
    {
        readonly List<T> _items = new List<T>();
        readonly IComparer<T> _cmp;
        public SortedSet() : this((IComparer<T>)null) { }
        public SortedSet(IComparer<T> comparer) { _cmp = comparer ?? Comparer<T>.Default; }
        public SortedSet(IEnumerable<T> collection) : this() { foreach (var x in collection) Add(x); }
        int Find(T x)
        {
            int lo = 0, hi = _items.Count - 1;
            while (lo <= hi) { int mid = lo + ((hi - lo) >> 1); int c = _cmp.Compare(_items[mid], x); if (c == 0) return mid; if (c < 0) lo = mid + 1; else hi = mid - 1; }
            return ~lo;
        }
        public int Count => _items.Count;
        public bool IsReadOnly => false;
        public T Min => _items.Count == 0 ? default : _items[0];
        public T Max => _items.Count == 0 ? default : _items[_items.Count - 1];
        public bool Add(T item) { int i = Find(item); if (i >= 0) return false; _items.Insert(~i, item); return true; }
        void ICollection<T>.Add(T item) => Add(item);
        public bool Remove(T item) { int i = Find(item); if (i < 0) return false; _items.RemoveAt(i); return true; }
        public bool Contains(T item) => Find(item) >= 0;
        public void Clear() => _items.Clear();
        public void CopyTo(T[] array, int index) => _items.CopyTo(array, index);
        public void UnionWith(IEnumerable<T> other) { foreach (var x in other) Add(x); }
        public void IntersectWith(IEnumerable<T> other) { var keep = new SortedSet<T>(_cmp); foreach (var x in other) if (Contains(x)) keep.Add(x); _items.Clear(); foreach (var x in keep) _items.Add(x); }
        public void ExceptWith(IEnumerable<T> other) { foreach (var x in other) Remove(x); }
        public bool IsSubsetOf(IEnumerable<T> other) { var o = new SortedSet<T>(other); foreach (var x in _items) if (!o.Contains(x)) return false; return true; }
        public bool IsSupersetOf(IEnumerable<T> other) { foreach (var x in other) if (!Contains(x)) return false; return true; }
        public bool Overlaps(IEnumerable<T> other) { foreach (var x in other) if (Contains(x)) return true; return false; }
        public bool SetEquals(IEnumerable<T> other) { var o = new SortedSet<T>(other); return o.Count == Count && IsSubsetOf(o); }
        public IEnumerable<T> Reverse() { for (int i = _items.Count - 1; i >= 0; i--) yield return _items[i]; }
        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

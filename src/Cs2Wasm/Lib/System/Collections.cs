// Collections. Dictionary and HashSet follow .NET's algorithm (entries array, free list, prime sizes)
// so that enumeration order is the same as under .NET.
#pragma warning disable CS0436
using Cs2Wasm.Runtime;

namespace System.Collections
{
    public interface IEnumerable { IEnumerator GetEnumerator(); }
    public interface IEnumerator { bool MoveNext(); object Current { get; } void Reset(); }
    public interface ICollection : IEnumerable { int Count { get; } }
    public interface IList : ICollection { object this[int index] { get; set; } }
    public interface IDictionary : ICollection { }
    public interface IEqualityComparer { bool Equals(object x, object y); int GetHashCode(object obj); }
    public interface IComparer { int Compare(object x, object y); }
    public interface IStructuralEquatable { }
}

namespace System.Collections.Generic
{
    public interface IEnumerable<out T> : IEnumerable { new IEnumerator<T> GetEnumerator(); }
    public interface IEnumerator<out T> : IDisposable, IEnumerator { new T Current { get; } }
    public interface IReadOnlyCollection<out T> : IEnumerable<T> { int Count { get; } }
    public interface IReadOnlyList<out T> : IReadOnlyCollection<T> { T this[int index] { get; } }
    public interface ICollection<T> : IEnumerable<T>
    {
        int Count { get; }
        bool IsReadOnly { get; }
        void Add(T item);
        void Clear();
        bool Contains(T item);
        void CopyTo(T[] array, int arrayIndex);
        bool Remove(T item);
    }
    public interface IList<T> : ICollection<T>
    {
        T this[int index] { get; set; }
        int IndexOf(T item);
        void Insert(int index, T item);
        void RemoveAt(int index);
    }
    public interface IReadOnlyDictionary<TKey, TValue> : IReadOnlyCollection<KeyValuePair<TKey, TValue>>
    {
        TValue this[TKey key] { get; }
        bool ContainsKey(TKey key);
        bool TryGetValue(TKey key, out TValue value);
    }
    public interface IDictionary<TKey, TValue> : ICollection<KeyValuePair<TKey, TValue>>
    {
        TValue this[TKey key] { get; set; }
        void Add(TKey key, TValue value);
        bool ContainsKey(TKey key);
        bool Remove(TKey key);
        bool TryGetValue(TKey key, out TValue value);
    }
    public interface ISet<T> : ICollection<T> { new bool Add(T item); }
    public interface IReadOnlySet<T> : IReadOnlyCollection<T> { bool Contains(T item); }
    public interface IEqualityComparer<in T> { bool Equals(T x, T y); int GetHashCode(T obj); }
    public interface IComparer<in T> { int Compare(T x, T y); }

    public readonly struct KeyValuePair<TKey, TValue>
    {
        readonly TKey key;
        readonly TValue value;
        public KeyValuePair(TKey key, TValue value) { this.key = key; this.value = value; }
        public TKey Key => key;
        public TValue Value => value;
        public override string ToString() => "[" + (key == null ? "" : key.ToString()) + ", " + (value == null ? "" : value.ToString()) + "]";
        public void Deconstruct(out TKey key, out TValue value) { key = this.key; value = this.value; }
    }

    public static class KeyValuePair
    {
        public static KeyValuePair<TKey, TValue> Create<TKey, TValue>(TKey key, TValue value) => new KeyValuePair<TKey, TValue>(key, value);
    }

    internal static class Generic
    {
        [Intrinsic] public static extern bool Equals<T>(T a, T b);
        [Intrinsic] public static extern int Hash<T>(T a);
        [Intrinsic] public static extern int Compare<T>(T a, T b);
        public static bool SlowEquals<T>(T a, T b) => a.Equals((object)b);
        public static int SlowCompare<T>(T a, T b)
        {
            if ((object)a is IComparable c) return c.CompareTo(b);
            CompareFailed();
            return 0;
        }
        public static void CompareFailed() =>
            throw new InvalidOperationException("Failed to compare two elements in the array.", new ArgumentException("At least one object must implement IComparable."));
    }

    public abstract class EqualityComparer<T> : IEqualityComparer<T>, IEqualityComparer
    {
        public static EqualityComparer<T> Default { get; } = new DefaultEqualityComparer<T>();
        public abstract bool Equals(T x, T y);
        public abstract int GetHashCode(T obj);
        bool IEqualityComparer.Equals(object x, object y) => Equals((T)x, (T)y);
        int IEqualityComparer.GetHashCode(object obj) => obj == null ? 0 : GetHashCode((T)obj);
        public static EqualityComparer<T> Create(Func<T, T, bool> equals, Func<T, int> getHashCode) => new DelegateEqualityComparer<T>(equals, getHashCode);
    }

    sealed class DefaultEqualityComparer<T> : EqualityComparer<T>
    {
        public override bool Equals(T x, T y) => Generic.Equals(x, y);
        public override int GetHashCode(T obj) => Generic.Hash(obj);
    }

    sealed class DelegateEqualityComparer<T> : EqualityComparer<T>
    {
        readonly Func<T, T, bool> _eq; readonly Func<T, int> _hash;
        public DelegateEqualityComparer(Func<T, T, bool> eq, Func<T, int> hash) { _eq = eq; _hash = hash; }
        public override bool Equals(T x, T y) => _eq(x, y);
        public override int GetHashCode(T obj) => _hash(obj);
    }

    public abstract class Comparer<T> : IComparer<T>, IComparer
    {
        public static Comparer<T> Default { get; } = new DefaultComparer<T>();
        public abstract int Compare(T x, T y);
        int IComparer.Compare(object x, object y) => Compare((T)x, (T)y);
        public static Comparer<T> Create(Comparison<T> comparison) => new ComparisonComparer<T>(comparison);
    }

    sealed class DefaultComparer<T> : Comparer<T>
    {
        public override int Compare(T x, T y) => Generic.Compare(x, y);
    }

    sealed class ComparisonComparer<T> : Comparer<T>
    {
        readonly Comparison<T> _c;
        public ComparisonComparer(Comparison<T> c) { _c = c; }
        public override int Compare(T x, T y) => _c(x, y);
    }

    internal static class ThrowHelper
    {
        public static void VersionChanged() => throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
        public static void EnumNotStarted() => throw new InvalidOperationException("Enumeration has either not started or has already finished.");
        public static void IndexOutOfRange(string name = "index") => throw new ArgumentOutOfRangeException(name, "Index was out of range. Must be non-negative and less than the size of the collection.");
        public static void KeyNotFound(object key) => throw new KeyNotFoundException("The given key '" + (key == null ? "" : key.ToString()) + "' was not present in the dictionary.");
        public static void DuplicateKey(object key) => throw new ArgumentException("An item with the same key has already been added. Key: " + (key == null ? "" : key.ToString()));
        public static void EmptyCollection() => throw new InvalidOperationException("Sequence contains no elements");
    }

    // ------------------------------------------------------------------ List<T>
    public class List<T> : IList<T>, IReadOnlyList<T>, IList
    {
        internal T[] _items;
        internal int _size;
        internal int _version;

        public List() { _items = Array.Empty<T>(); }
        public List(int capacity)
        {
            if (capacity < 0) throw new ArgumentOutOfRangeException("capacity", "Non-negative number required.");
            _items = capacity == 0 ? Array.Empty<T>() : new T[capacity];
        }
        public List(IEnumerable<T> collection)
        {
            if (collection == null) throw new ArgumentNullException("collection");
            _items = Array.Empty<T>();
            if (collection is ICollection<T> c)
            {
                int count = c.Count;
                if (count > 0) { _items = new T[count]; c.CopyTo(_items, 0); _size = count; }
                return;
            }
            foreach (var item in collection) Add(item);
        }

        public int Capacity
        {
            get => _items.Length;
            set
            {
                if (value < _size) throw new ArgumentOutOfRangeException("value", "capacity was less than the current size.");
                if (value == _items.Length) return;
                var n = value > 0 ? new T[value] : Array.Empty<T>();
                for (int i = 0; i < _size; i++) n[i] = _items[i];
                _items = n;
            }
        }
        public int Count => _size;
        public bool IsReadOnly => false;

        public T this[int index]
        {
            get { if ((uint)index >= (uint)_size) ThrowHelper.IndexOutOfRange(); return _items[index]; }
            set { if ((uint)index >= (uint)_size) ThrowHelper.IndexOutOfRange(); _items[index] = value; _version++; }
        }
        object IList.this[int index] { get => this[index]; set => this[index] = (T)value; }

        public void Add(T item)
        {
            _version++;
            if (_size == _items.Length) Grow(_size + 1);
            _items[_size++] = item;
        }

        void Grow(int capacity)
        {
            int newCapacity = _items.Length == 0 ? 4 : 2 * _items.Length;
            if ((uint)newCapacity > 0x7FFFFFC7) newCapacity = 0x7FFFFFC7;
            if (newCapacity < capacity) newCapacity = capacity;
            Capacity = newCapacity;
        }

        public void EnsureCapacity(int capacity) { if (_items.Length < capacity) Grow(capacity); }

        public void AddRange(IEnumerable<T> collection)
        {
            if (collection == null) throw new ArgumentNullException("collection");
            if (collection is ICollection<T> c)
            {
                int count = c.Count;
                if (count > 0)
                {
                    if (_items.Length - _size < count) Grow(_size + count);
                    if (Rt.AddressOf(c) == Rt.AddressOf(this)) { for (int i = 0; i < count; i++) _items[_size + i] = _items[i]; }
                    else c.CopyTo(_items, _size);
                    _size += count;
                    _version++;
                }
                return;
            }
            foreach (var item in collection) Add(item);
        }

        public void Clear()
        {
            _version++;
            if (Rt.ContainsReferences<T>()) for (int i = 0; i < _size; i++) _items[i] = default;
            _size = 0;
        }

        public bool Contains(T item) => _size != 0 && IndexOf(item) >= 0;
        public int IndexOf(T item)
        {
            for (int i = 0; i < _size; i++) if (Generic.Equals(_items[i], item)) return i;
            return -1;
        }
        public int IndexOf(T item, int index)
        {
            for (int i = index; i < _size; i++) if (Generic.Equals(_items[i], item)) return i;
            return -1;
        }
        public int LastIndexOf(T item)
        {
            for (int i = _size - 1; i >= 0; i--) if (Generic.Equals(_items[i], item)) return i;
            return -1;
        }

        public void Insert(int index, T item)
        {
            if ((uint)index > (uint)_size) throw new ArgumentOutOfRangeException("index", "Index must be within the bounds of the List.");
            if (_size == _items.Length) Grow(_size + 1);
            for (int i = _size; i > index; i--) _items[i] = _items[i - 1];
            _items[index] = item;
            _size++;
            _version++;
        }
        public void InsertRange(int index, IEnumerable<T> collection)
        {
            var tmp = new List<T>(collection);
            for (int i = 0; i < tmp.Count; i++) Insert(index + i, tmp[i]);
        }

        public bool Remove(T item)
        {
            int index = IndexOf(item);
            if (index >= 0) { RemoveAt(index); return true; }
            return false;
        }
        public void RemoveAt(int index)
        {
            if ((uint)index >= (uint)_size) ThrowHelper.IndexOutOfRange();
            _size--;
            for (int i = index; i < _size; i++) _items[i] = _items[i + 1];
            _items[_size] = default;
            _version++;
        }
        public void RemoveRange(int index, int count)
        {
            if (index < 0 || count < 0 || _size - index < count) throw new ArgumentException("Offset and length were out of bounds for the array or count is greater than the number of elements from index to the end of the source collection.");
            if (count == 0) return;
            _size -= count;
            for (int i = index; i < _size; i++) _items[i] = _items[i + count];
            for (int i = _size; i < _size + count; i++) _items[i] = default;
            _version++;
        }
        public int RemoveAll(Predicate<T> match)
        {
            int free = 0;
            while (free < _size && !match(_items[free])) free++;
            if (free >= _size) return 0;
            int current = free + 1;
            while (current < _size)
            {
                while (current < _size && match(_items[current])) current++;
                if (current < _size) _items[free++] = _items[current++];
            }
            for (int i = free; i < _size; i++) _items[i] = default;
            int result = _size - free;
            _size = free;
            _version++;
            return result;
        }

        public void CopyTo(T[] array) => CopyTo(array, 0);
        public void CopyTo(T[] array, int arrayIndex)
        {
            if (array == null) throw new ArgumentNullException("array");
            if (arrayIndex < 0 || array.Length - arrayIndex < _size) throw new ArgumentException("Destination array was not long enough. Check the destination index, length, and the array's lower bounds.");
            for (int i = 0; i < _size; i++) array[arrayIndex + i] = _items[i];
        }
        public T[] ToArray()
        {
            var a = new T[_size];
            for (int i = 0; i < _size; i++) a[i] = _items[i];
            return a;
        }
        public List<T> GetRange(int index, int count)
        {
            if (index < 0 || count < 0 || _size - index < count) throw new ArgumentException("Offset and length were out of bounds for the array or count is greater than the number of elements from index to the end of the source collection.");
            var l = new List<T>(count);
            for (int i = 0; i < count; i++) l._items[i] = _items[index + i];
            l._size = count;
            return l;
        }
        public List<T> Slice(int start, int length) => GetRange(start, length);

        public void Reverse() => Reverse(0, _size);
        public void Reverse(int index, int count)
        {
            Array.Reverse(_items, index, count);
            _version++;
        }
        public void Sort() => Sort(0, _size, null);
        public void Sort(IComparer<T> comparer) => Sort(0, _size, comparer);
        public void Sort(int index, int count, IComparer<T> comparer)
        {
            if (count > 1) Array.Sort(_items, index, count, comparer);
            _version++;
        }
        public void Sort(Comparison<T> comparison)
        {
            if (comparison == null) throw new ArgumentNullException("comparison");
            if (_size > 1) ArraySortHelper<T>.Sort(_items, 0, _size, comparison);
            _version++;
        }
        public int BinarySearch(T item)
        {
            int lo = 0, hi = _size - 1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                int c = Generic.Compare(_items[mid], item);
                if (c == 0) return mid;
                if (c < 0) lo = mid + 1; else hi = mid - 1;
            }
            return ~lo;
        }

        public bool Exists(Predicate<T> match) => FindIndex(match) != -1;
        public T Find(Predicate<T> match)
        {
            for (int i = 0; i < _size; i++) if (match(_items[i])) return _items[i];
            return default;
        }
        public List<T> FindAll(Predicate<T> match)
        {
            var l = new List<T>();
            for (int i = 0; i < _size; i++) if (match(_items[i])) l.Add(_items[i]);
            return l;
        }
        public int FindIndex(Predicate<T> match)
        {
            for (int i = 0; i < _size; i++) if (match(_items[i])) return i;
            return -1;
        }
        public T FindLast(Predicate<T> match)
        {
            for (int i = _size - 1; i >= 0; i--) if (match(_items[i])) return _items[i];
            return default;
        }
        public int FindLastIndex(Predicate<T> match)
        {
            for (int i = _size - 1; i >= 0; i--) if (match(_items[i])) return i;
            return -1;
        }
        public bool TrueForAll(Predicate<T> match)
        {
            for (int i = 0; i < _size; i++) if (!match(_items[i])) return false;
            return true;
        }
        public void ForEach(Action<T> action)
        {
            int version = _version;
            for (int i = 0; i < _size; i++)
            {
                if (version != _version) break;
                action(_items[i]);
            }
            if (version != _version) ThrowHelper.VersionChanged();
        }
        public List<TOutput> ConvertAll<TOutput>(Converter<T, TOutput> converter)
        {
            var l = new List<TOutput>(_size);
            for (int i = 0; i < _size; i++) l._items[i] = converter(_items[i]);
            l._size = _size;
            return l;
        }
        public void TrimExcess() { if (_size < (int)(_items.Length * 0.9)) Capacity = _size; }

        public Enumerator GetEnumerator() => new Enumerator(this);
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => new BoxedEnumerator(this);
        IEnumerator IEnumerable.GetEnumerator() => new BoxedEnumerator(this);

        public struct Enumerator : IEnumerator<T>
        {
            readonly List<T> _list;
            int _index;
            readonly int _version;
            T _current;

            internal Enumerator(List<T> list) { _list = list; _index = 0; _version = list._version; _current = default; }
            public void Dispose() { }
            public bool MoveNext()
            {
                var l = _list;
                if (_version == l._version && ((uint)_index < (uint)l._size))
                {
                    _current = l._items[_index];
                    _index++;
                    return true;
                }
                if (_version != _list._version) ThrowHelper.VersionChanged();
                _index = _list._size + 1;
                _current = default;
                return false;
            }
            public T Current => _current;
            object IEnumerator.Current => _current;
            void IEnumerator.Reset() { _index = 0; _current = default; }
        }

        sealed class BoxedEnumerator : IEnumerator<T>
        {
            Enumerator _e;
            public BoxedEnumerator(List<T> l) { _e = new Enumerator(l); }
            public bool MoveNext() => _e.MoveNext();
            public T Current => _e.Current;
            object IEnumerator.Current => _e.Current;
            public void Reset() => ((IEnumerator)_e).Reset();
            public void Dispose() { }
        }

        int ICollection.Count => _size;
        int IReadOnlyCollection<T>.Count => _size;
        int ICollection<T>.Count => _size;
    }

    internal static class HashHelpers
    {
        internal static readonly int[] Primes = [
            3, 7, 11, 17, 23, 29, 37, 47, 59, 71, 89, 107, 131, 163, 197, 239, 293, 353, 431, 521, 631, 761, 919,
            1103, 1327, 1597, 1931, 2333, 2801, 3371, 4049, 4861, 5839, 7013, 8419, 10103, 12143, 14591,
            17519, 21023, 25229, 30293, 36353, 43627, 52361, 62851, 75431, 90523, 108631, 130363, 156437,
            187751, 225307, 270371, 324449, 389357, 467237, 560689, 672827, 807403, 968897, 1162687, 1395263,
            1674319, 2009191, 2411033, 2893249, 3471899, 4166287, 4999559, 5999471, 7199369];

        static bool IsPrime(int candidate)
        {
            if ((candidate & 1) != 0)
            {
                int limit = (int)Math.Sqrt(candidate);
                for (int divisor = 3; divisor <= limit; divisor += 2) if ((candidate % divisor) == 0) return false;
                return true;
            }
            return candidate == 2;
        }

        public static int GetPrime(int min)
        {
            var p = Primes;
            for (int i = 0; i < p.Length; i++) if (p[i] >= min) return p[i];
            for (int i = (min | 1); i < int.MaxValue; i += 2)
                if (IsPrime(i) && ((i - 1) % 101 != 0)) return i;
            return min;
        }

        public static int ExpandPrime(int oldSize)
        {
            int newSize = 2 * oldSize;
            if ((uint)newSize > 0x7FFFFFC3 && 0x7FFFFFC3 > oldSize) return 0x7FFFFFC3;
            return GetPrime(newSize);
        }
    }

    // ------------------------------------------------------------------ Dictionary<TKey, TValue>
    public class Dictionary<TKey, TValue> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>, IDictionary
    {
        struct Entry
        {
            public uint hashCode;
            public int next; // 0-based index of next entry in chain: -1 means end of chain; below -1 encodes the free list
            public TKey key;
            public TValue value;
        }

        int[] _buckets;
        Entry[] _entries;
        int _count;
        int _freeList;
        int _freeCount;
        int _version;
        readonly IEqualityComparer<TKey> _comparer;
        KeyCollection _keys;
        ValueCollection _values;
        const int StartOfFreeList = -3;

        public Dictionary() { }
        public Dictionary(int capacity) { if (capacity < 0) throw new ArgumentOutOfRangeException("capacity"); if (capacity > 0) Initialize(capacity); }
        public Dictionary(IEqualityComparer<TKey> comparer) { _comparer = comparer; }
        public Dictionary(int capacity, IEqualityComparer<TKey> comparer) : this(capacity) { _comparer = comparer; }
        public Dictionary(IDictionary<TKey, TValue> dictionary) : this(dictionary == null ? 0 : dictionary.Count)
        {
            if (dictionary == null) throw new ArgumentNullException("dictionary");
            foreach (var kv in dictionary) Add(kv.Key, kv.Value);
        }
        public Dictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection)
        {
            foreach (var kv in collection) Add(kv.Key, kv.Value);
        }

        public int Count => _count - _freeCount;
        public bool IsReadOnly => false;
        public IEqualityComparer<TKey> Comparer => _comparer ?? EqualityComparer<TKey>.Default;

        int Initialize(int capacity)
        {
            int size = HashHelpers.GetPrime(capacity);
            _freeList = -1;
            _buckets = new int[size];
            _entries = new Entry[size];
            return size;
        }

        uint HashOf(TKey key) => (uint)(_comparer == null ? Generic.Hash(key) : _comparer.GetHashCode(key));
        bool Eq(TKey a, TKey b) => _comparer == null ? Generic.Equals(a, b) : _comparer.Equals(a, b);

        int FindValue(TKey key)
        {
            if (key == null) throw new ArgumentNullException("key");
            if (_buckets == null) return -1;
            uint hashCode = HashOf(key);
            int i = _buckets[(int)(hashCode % (uint)_buckets.Length)] - 1;
            var entries = _entries;
            uint collisions = 0;
            while ((uint)i < (uint)entries.Length)
            {
                if (entries[i].hashCode == hashCode && Eq(entries[i].key, key)) return i;
                i = entries[i].next;
                collisions++;
                if (collisions > (uint)entries.Length) throw new InvalidOperationException("Operations that change non-concurrent collections must have exclusive access.");
            }
            return -1;
        }

        public TValue this[TKey key]
        {
            get
            {
                int i = FindValue(key);
                if (i >= 0) return _entries[i].value;
                ThrowHelper.KeyNotFound(key);
                return default;
            }
            set => TryInsert(key, value, true);
        }

        public void Add(TKey key, TValue value) => TryInsert(key, value, false);
        public bool TryAdd(TKey key, TValue value)
        {
            if (key == null) throw new ArgumentNullException("key");
            if (FindValue(key) >= 0) return false;
            TryInsert(key, value, false);
            return true;
        }

        bool TryInsert(TKey key, TValue value, bool overwrite)
        {
            if (key == null) throw new ArgumentNullException("key");
            if (_buckets == null) Initialize(0);
            var entries = _entries;
            uint hashCode = HashOf(key);
            uint collisions = 0;
            int bucketIndex = (int)(hashCode % (uint)_buckets.Length);
            int i = _buckets[bucketIndex] - 1;
            while ((uint)i < (uint)entries.Length)
            {
                if (entries[i].hashCode == hashCode && Eq(entries[i].key, key))
                {
                    if (overwrite) { entries[i].value = value; return true; }
                    ThrowHelper.DuplicateKey(key);
                }
                i = entries[i].next;
                collisions++;
                if (collisions > (uint)entries.Length) throw new InvalidOperationException("Operations that change non-concurrent collections must have exclusive access.");
            }
            int index;
            if (_freeCount > 0)
            {
                index = _freeList;
                _freeList = StartOfFreeList - entries[_freeList].next;
                _freeCount--;
            }
            else
            {
                int count = _count;
                if (count == entries.Length)
                {
                    Resize(HashHelpers.ExpandPrime(count));
                    bucketIndex = (int)(hashCode % (uint)_buckets.Length);
                }
                index = count;
                _count = count + 1;
                entries = _entries;
            }
            entries[index].hashCode = hashCode;
            entries[index].next = _buckets[bucketIndex] - 1;
            entries[index].key = key;
            entries[index].value = value;
            _buckets[bucketIndex] = index + 1;
            _version++;
            return true;
        }

        void Resize(int newSize)
        {
            var entries = new Entry[newSize];
            int count = _count;
            for (int i = 0; i < count; i++) entries[i] = _entries[i];
            _buckets = new int[newSize];
            for (int i = 0; i < count; i++)
            {
                if (entries[i].next >= -1)
                {
                    int bucket = (int)(entries[i].hashCode % (uint)newSize);
                    entries[i].next = _buckets[bucket] - 1;
                    _buckets[bucket] = i + 1;
                }
            }
            _entries = entries;
        }

        public bool Remove(TKey key) => Remove(key, out _);
        public bool Remove(TKey key, out TValue value)
        {
            if (key == null) throw new ArgumentNullException("key");
            if (_buckets != null)
            {
                var entries = _entries;
                uint collisions = 0;
                uint hashCode = HashOf(key);
                int bucket = (int)(hashCode % (uint)_buckets.Length);
                int last = -1;
                int i = _buckets[bucket] - 1;
                while (i >= 0)
                {
                    if (entries[i].hashCode == hashCode && Eq(entries[i].key, key))
                    {
                        if (last < 0) _buckets[bucket] = entries[i].next + 1;
                        else entries[last].next = entries[i].next;
                        value = entries[i].value;
                        entries[i].next = StartOfFreeList - _freeList;
                        entries[i].key = default;
                        entries[i].value = default;
                        _freeList = i;
                        _freeCount++;
                        return true;
                    }
                    last = i;
                    i = entries[i].next;
                    collisions++;
                    if (collisions > (uint)entries.Length) throw new InvalidOperationException("Operations that change non-concurrent collections must have exclusive access.");
                }
            }
            value = default;
            return false;
        }

        public void Clear()
        {
            int count = _count;
            if (count > 0)
            {
                for (int i = 0; i < _buckets.Length; i++) _buckets[i] = 0;
                _count = 0; _freeList = -1; _freeCount = 0;
                for (int i = 0; i < count; i++) _entries[i] = default;
            }
        }

        public bool ContainsKey(TKey key) => FindValue(key) >= 0;
        public bool ContainsValue(TValue value)
        {
            for (int i = 0; i < _count; i++)
                if (_entries[i].next >= -1 && Generic.Equals(_entries[i].value, value)) return true;
            return false;
        }
        public bool TryGetValue(TKey key, out TValue value)
        {
            int i = FindValue(key);
            if (i >= 0) { value = _entries[i].value; return true; }
            value = default;
            return false;
        }
        public TValue GetValueOrDefault(TKey key) => TryGetValue(key, out var v) ? v : default;
        public TValue GetValueOrDefault(TKey key, TValue defaultValue) => TryGetValue(key, out var v) ? v : defaultValue;

        void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);
        bool ICollection<KeyValuePair<TKey, TValue>>.Contains(KeyValuePair<TKey, TValue> item)
        {
            int i = FindValue(item.Key);
            return i >= 0 && Generic.Equals(_entries[i].value, item.Value);
        }
        bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> item) => Remove(item.Key);
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int index)
        {
            for (int i = 0; i < _count; i++)
                if (_entries[i].next >= -1) array[index++] = new KeyValuePair<TKey, TValue>(_entries[i].key, _entries[i].value);
        }

        public KeyCollection Keys => _keys ??= new KeyCollection(this);
        public ValueCollection Values => _values ??= new ValueCollection(this);

        public Enumerator GetEnumerator() => new Enumerator(this);
        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => new BoxedEnumerator(this);
        IEnumerator IEnumerable.GetEnumerator() => new BoxedEnumerator(this);

        int ICollection.Count => Count;
        int IReadOnlyCollection<KeyValuePair<TKey, TValue>>.Count => Count;
        int ICollection<KeyValuePair<TKey, TValue>>.Count => Count;

        public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
        {
            readonly Dictionary<TKey, TValue> _d;
            readonly int _version;
            int _index;
            KeyValuePair<TKey, TValue> _current;
            internal Enumerator(Dictionary<TKey, TValue> d) { _d = d; _version = d._version; _index = 0; _current = default; }
            public bool MoveNext()
            {
                if (_version != _d._version) ThrowHelper.VersionChanged();
                while ((uint)_index < (uint)_d._count)
                {
                    int i = _index++;
                    if (_d._entries[i].next >= -1)
                    {
                        _current = new KeyValuePair<TKey, TValue>(_d._entries[i].key, _d._entries[i].value);
                        return true;
                    }
                }
                _index = _d._count + 1;
                _current = default;
                return false;
            }
            public KeyValuePair<TKey, TValue> Current => _current;
            object IEnumerator.Current => _current;
            void IEnumerator.Reset() { _index = 0; _current = default; }
            public void Dispose() { }
        }

        sealed class BoxedEnumerator : IEnumerator<KeyValuePair<TKey, TValue>>
        {
            Enumerator _e;
            public BoxedEnumerator(Dictionary<TKey, TValue> d) { _e = new Enumerator(d); }
            public bool MoveNext() => _e.MoveNext();
            public KeyValuePair<TKey, TValue> Current => _e.Current;
            object IEnumerator.Current => _e.Current;
            public void Reset() => ((IEnumerator)_e).Reset();
            public void Dispose() { }
        }

        public sealed class KeyCollection : ICollection<TKey>, IReadOnlyCollection<TKey>
        {
            readonly Dictionary<TKey, TValue> _d;
            public KeyCollection(Dictionary<TKey, TValue> d) { _d = d; }
            public int Count => _d.Count;
            public bool IsReadOnly => true;
            public void Add(TKey item) => throw new NotSupportedException("Mutating a key collection derived from a dictionary is not allowed.");
            public void Clear() => throw new NotSupportedException("Mutating a key collection derived from a dictionary is not allowed.");
            public bool Contains(TKey item) => _d.ContainsKey(item);
            public bool Remove(TKey item) => throw new NotSupportedException("Mutating a key collection derived from a dictionary is not allowed.");
            public void CopyTo(TKey[] array, int index)
            {
                for (int i = 0; i < _d._count; i++) if (_d._entries[i].next >= -1) array[index++] = _d._entries[i].key;
            }
            public Enumerator GetEnumerator() => new Enumerator(_d);
            IEnumerator<TKey> IEnumerable<TKey>.GetEnumerator() => new Boxed(_d);
            IEnumerator IEnumerable.GetEnumerator() => new Boxed(_d);
            int IReadOnlyCollection<TKey>.Count => _d.Count;
            int ICollection<TKey>.Count => _d.Count;

            public struct Enumerator : IEnumerator<TKey>
            {
                Dictionary<TKey, TValue>.Enumerator _e;
                internal Enumerator(Dictionary<TKey, TValue> d) { _e = new Dictionary<TKey, TValue>.Enumerator(d); }
                public bool MoveNext() => _e.MoveNext();
                public TKey Current => _e.Current.Key;
                object IEnumerator.Current => Current;
                void IEnumerator.Reset() => ((IEnumerator)_e).Reset();
                public void Dispose() { }
            }
            sealed class Boxed : IEnumerator<TKey>
            {
                Enumerator _e;
                public Boxed(Dictionary<TKey, TValue> d) { _e = new Enumerator(d); }
                public bool MoveNext() => _e.MoveNext();
                public TKey Current => _e.Current;
                object IEnumerator.Current => _e.Current;
                public void Reset() { }
                public void Dispose() { }
            }
        }

        public sealed class ValueCollection : ICollection<TValue>, IReadOnlyCollection<TValue>
        {
            readonly Dictionary<TKey, TValue> _d;
            public ValueCollection(Dictionary<TKey, TValue> d) { _d = d; }
            public int Count => _d.Count;
            public bool IsReadOnly => true;
            public void Add(TValue item) => throw new NotSupportedException("Mutating a value collection derived from a dictionary is not allowed.");
            public void Clear() => throw new NotSupportedException("Mutating a value collection derived from a dictionary is not allowed.");
            public bool Contains(TValue item) => _d.ContainsValue(item);
            public bool Remove(TValue item) => throw new NotSupportedException("Mutating a value collection derived from a dictionary is not allowed.");
            public void CopyTo(TValue[] array, int index)
            {
                for (int i = 0; i < _d._count; i++) if (_d._entries[i].next >= -1) array[index++] = _d._entries[i].value;
            }
            public Enumerator GetEnumerator() => new Enumerator(_d);
            IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator() => new Boxed(_d);
            IEnumerator IEnumerable.GetEnumerator() => new Boxed(_d);
            int IReadOnlyCollection<TValue>.Count => _d.Count;
            int ICollection<TValue>.Count => _d.Count;

            public struct Enumerator : IEnumerator<TValue>
            {
                Dictionary<TKey, TValue>.Enumerator _e;
                internal Enumerator(Dictionary<TKey, TValue> d) { _e = new Dictionary<TKey, TValue>.Enumerator(d); }
                public bool MoveNext() => _e.MoveNext();
                public TValue Current => _e.Current.Value;
                object IEnumerator.Current => Current;
                void IEnumerator.Reset() => ((IEnumerator)_e).Reset();
                public void Dispose() { }
            }
            sealed class Boxed : IEnumerator<TValue>
            {
                Enumerator _e;
                public Boxed(Dictionary<TKey, TValue> d) { _e = new Enumerator(d); }
                public bool MoveNext() => _e.MoveNext();
                public TValue Current => _e.Current;
                object IEnumerator.Current => _e.Current;
                public void Reset() { }
                public void Dispose() { }
            }
        }
    }

    // ------------------------------------------------------------------ HashSet<T>
    public class HashSet<T> : ISet<T>, IReadOnlySet<T>, IReadOnlyCollection<T>
    {
        struct Entry { public uint hashCode; public int next; public T value; }
        int[] _buckets;
        Entry[] _entries;
        int _count, _freeList, _freeCount, _version;
        readonly IEqualityComparer<T> _comparer;
        const int StartOfFreeList = -3;

        public HashSet() { }
        public HashSet(IEqualityComparer<T> comparer) { _comparer = comparer; }
        public HashSet(IEnumerable<T> collection) { foreach (var x in collection) Add(x); }
        public HashSet(IEnumerable<T> collection, IEqualityComparer<T> comparer) { _comparer = comparer; foreach (var x in collection) Add(x); }
        public HashSet(int capacity) { if (capacity > 0) Initialize(capacity); }

        public int Count => _count - _freeCount;
        public bool IsReadOnly => false;

        void Initialize(int capacity)
        {
            int size = HashHelpers.GetPrime(capacity);
            _freeList = -1;
            _buckets = new int[size];
            _entries = new Entry[size];
        }
        uint HashOf(T v) => v == null ? 0 : (uint)(_comparer == null ? Generic.Hash(v) : _comparer.GetHashCode(v));
        bool Eq(T a, T b) => _comparer == null ? Generic.Equals(a, b) : _comparer.Equals(a, b);

        int FindItemIndex(T item)
        {
            if (_buckets == null) return -1;
            uint hashCode = HashOf(item);
            int i = _buckets[(int)(hashCode % (uint)_buckets.Length)] - 1;
            while (i >= 0)
            {
                if (_entries[i].hashCode == hashCode && Eq(_entries[i].value, item)) return i;
                i = _entries[i].next;
            }
            return -1;
        }

        public bool Contains(T item) => FindItemIndex(item) >= 0;

        public bool Add(T value)
        {
            if (_buckets == null) Initialize(0);
            uint hashCode = HashOf(value);
            int bucket = (int)(hashCode % (uint)_buckets.Length);
            int i = _buckets[bucket] - 1;
            while (i >= 0)
            {
                if (_entries[i].hashCode == hashCode && Eq(_entries[i].value, value)) return false;
                i = _entries[i].next;
            }
            int index;
            if (_freeCount > 0)
            {
                index = _freeList;
                _freeCount--;
                _freeList = StartOfFreeList - _entries[_freeList].next;
            }
            else
            {
                int count = _count;
                if (count == _entries.Length)
                {
                    Resize(HashHelpers.ExpandPrime(count));
                    bucket = (int)(hashCode % (uint)_buckets.Length);
                }
                index = count;
                _count = count + 1;
            }
            _entries[index].hashCode = hashCode;
            _entries[index].next = _buckets[bucket] - 1;
            _entries[index].value = value;
            _buckets[bucket] = index + 1;
            _version++;
            return true;
        }
        void ICollection<T>.Add(T item) => Add(item);

        void Resize(int newSize)
        {
            var entries = new Entry[newSize];
            for (int i = 0; i < _count; i++) entries[i] = _entries[i];
            _buckets = new int[newSize];
            for (int i = 0; i < _count; i++)
            {
                if (entries[i].next >= -1)
                {
                    int bucket = (int)(entries[i].hashCode % (uint)newSize);
                    entries[i].next = _buckets[bucket] - 1;
                    _buckets[bucket] = i + 1;
                }
            }
            _entries = entries;
        }

        public bool Remove(T item)
        {
            if (_buckets == null) return false;
            uint hashCode = HashOf(item);
            int bucket = (int)(hashCode % (uint)_buckets.Length);
            int last = -1;
            int i = _buckets[bucket] - 1;
            while (i >= 0)
            {
                if (_entries[i].hashCode == hashCode && Eq(_entries[i].value, item))
                {
                    if (last < 0) _buckets[bucket] = _entries[i].next + 1;
                    else _entries[last].next = _entries[i].next;
                    _entries[i].next = StartOfFreeList - _freeList;
                    _entries[i].value = default;
                    _freeList = i;
                    _freeCount++;
                    return true;
                }
                last = i;
                i = _entries[i].next;
            }
            return false;
        }

        public void Clear()
        {
            if (_count > 0)
            {
                for (int i = 0; i < _buckets.Length; i++) _buckets[i] = 0;
                for (int i = 0; i < _count; i++) _entries[i] = default;
                _count = 0; _freeList = -1; _freeCount = 0;
            }
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            for (int i = 0; i < _count; i++) if (_entries[i].next >= -1) array[arrayIndex++] = _entries[i].value;
        }
        public void UnionWith(IEnumerable<T> other) { foreach (var x in other) Add(x); }
        public void IntersectWith(IEnumerable<T> other)
        {
            var keep = new HashSet<T>(other, _comparer);
            for (int i = 0; i < _count; i++) if (_entries[i].next >= -1 && !keep.Contains(_entries[i].value)) Remove(_entries[i].value);
        }
        public void ExceptWith(IEnumerable<T> other) { foreach (var x in other) Remove(x); }
        public bool Overlaps(IEnumerable<T> other) { foreach (var x in other) if (Contains(x)) return true; return false; }
        public bool SetEquals(IEnumerable<T> other)
        {
            var o = new HashSet<T>(other, _comparer);
            if (o.Count != Count) return false;
            foreach (var x in o) if (!Contains(x)) return false;
            return true;
        }
        public bool IsSubsetOf(IEnumerable<T> other)
        {
            var o = new HashSet<T>(other, _comparer);
            for (int i = 0; i < _count; i++) if (_entries[i].next >= -1 && !o.Contains(_entries[i].value)) return false;
            return true;
        }
        public bool IsSupersetOf(IEnumerable<T> other) { foreach (var x in other) if (!Contains(x)) return false; return true; }
        public int RemoveWhere(Predicate<T> match)
        {
            int n = 0;
            for (int i = 0; i < _count; i++)
                if (_entries[i].next >= -1) { var v = _entries[i].value; if (match(v)) { Remove(v); n++; } }
            return n;
        }

        public Enumerator GetEnumerator() => new Enumerator(this);
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => new Boxed(this);
        IEnumerator IEnumerable.GetEnumerator() => new Boxed(this);
        int ICollection<T>.Count => Count;
        int IReadOnlyCollection<T>.Count => Count;

        public struct Enumerator : IEnumerator<T>
        {
            readonly HashSet<T> _s; readonly int _version; int _index; T _current;
            internal Enumerator(HashSet<T> s) { _s = s; _version = s._version; _index = 0; _current = default; }
            public bool MoveNext()
            {
                if (_version != _s._version) ThrowHelper.VersionChanged();
                while ((uint)_index < (uint)_s._count)
                {
                    int i = _index++;
                    if (_s._entries[i].next >= -1) { _current = _s._entries[i].value; return true; }
                }
                _index = _s._count + 1;
                _current = default;
                return false;
            }
            public T Current => _current;
            object IEnumerator.Current => _current;
            void IEnumerator.Reset() { _index = 0; _current = default; }
            public void Dispose() { }
        }
        sealed class Boxed : IEnumerator<T>
        {
            Enumerator _e;
            public Boxed(HashSet<T> s) { _e = new Enumerator(s); }
            public bool MoveNext() => _e.MoveNext();
            public T Current => _e.Current;
            object IEnumerator.Current => _e.Current;
            public void Reset() { }
            public void Dispose() { }
        }
    }

    // ------------------------------------------------------------------ Queue<T>, Stack<T>
    public class Queue<T> : IReadOnlyCollection<T>
    {
        T[] _array = Array.Empty<T>();
        int _head, _tail, _size, _version;
        public Queue() { }
        public Queue(int capacity) { _array = new T[capacity]; }
        public Queue(IEnumerable<T> collection) { foreach (var x in collection) Enqueue(x); }
        public int Count => _size;
        public void Enqueue(T item)
        {
            if (_size == _array.Length)
            {
                int newcapacity = (int)((long)_array.Length * 2);
                if (newcapacity < _array.Length + 4) newcapacity = _array.Length + 4;
                SetCapacity(newcapacity);
            }
            _array[_tail] = item;
            _tail = (_tail + 1) % _array.Length;
            _size++;
            _version++;
        }
        public T Dequeue()
        {
            if (_size == 0) throw new InvalidOperationException("Queue empty.");
            T removed = _array[_head];
            _array[_head] = default;
            _head = (_head + 1) % _array.Length;
            _size--;
            _version++;
            return removed;
        }
        public bool TryDequeue(out T result)
        {
            if (_size == 0) { result = default; return false; }
            result = Dequeue(); return true;
        }
        public T Peek()
        {
            if (_size == 0) throw new InvalidOperationException("Queue empty.");
            return _array[_head];
        }
        public bool TryPeek(out T result)
        {
            if (_size == 0) { result = default; return false; }
            result = _array[_head]; return true;
        }
        public bool Contains(T item)
        {
            for (int i = 0; i < _size; i++) if (Generic.Equals(_array[(_head + i) % _array.Length], item)) return true;
            return false;
        }
        public void Clear() { for (int i = 0; i < _array.Length; i++) _array[i] = default; _head = _tail = _size = 0; _version++; }
        public T[] ToArray()
        {
            var a = new T[_size];
            for (int i = 0; i < _size; i++) a[i] = _array[(_head + i) % _array.Length];
            return a;
        }
        void SetCapacity(int capacity)
        {
            var n = new T[capacity];
            for (int i = 0; i < _size; i++) n[i] = _array[(_head + i) % _array.Length];
            _array = n;
            _head = 0;
            _tail = _size == capacity ? 0 : _size;
        }
        public IEnumerator<T> GetEnumerator()
        {
            var a = ToArray();
            return ((IEnumerable<T>)new List<T>(a)).GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public class Stack<T> : IReadOnlyCollection<T>
    {
        T[] _array = Array.Empty<T>();
        int _size, _version;
        public Stack() { }
        public Stack(int capacity) { _array = new T[capacity]; }
        public Stack(IEnumerable<T> collection) { foreach (var x in collection) Push(x); }
        public int Count => _size;
        public void Push(T item)
        {
            if (_size == _array.Length)
            {
                var n = new T[_array.Length == 0 ? 4 : 2 * _array.Length];
                for (int i = 0; i < _size; i++) n[i] = _array[i];
                _array = n;
            }
            _array[_size++] = item;
            _version++;
        }
        public T Pop()
        {
            if (_size == 0) throw new InvalidOperationException("Stack empty.");
            _version++;
            T item = _array[--_size];
            _array[_size] = default;
            return item;
        }
        public bool TryPop(out T result)
        {
            if (_size == 0) { result = default; return false; }
            result = Pop(); return true;
        }
        public T Peek()
        {
            if (_size == 0) throw new InvalidOperationException("Stack empty.");
            return _array[_size - 1];
        }
        public bool TryPeek(out T result)
        {
            if (_size == 0) { result = default; return false; }
            result = _array[_size - 1]; return true;
        }
        public bool Contains(T item)
        {
            for (int i = 0; i < _size; i++) if (Generic.Equals(_array[i], item)) return true;
            return false;
        }
        public void Clear() { for (int i = 0; i < _size; i++) _array[i] = default; _size = 0; _version++; }
        public T[] ToArray()
        {
            var a = new T[_size];
            for (int i = 0; i < _size; i++) a[i] = _array[_size - 1 - i];
            return a;
        }
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)new List<T>(ToArray())).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

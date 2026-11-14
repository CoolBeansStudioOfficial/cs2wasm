#pragma warning disable CS0436
// PriorityQueue (a quaternary min-heap, as .NET's, so equal priorities come out in the same order), LinkedList,
// lookups and joins.
using System.Collections;
using System.Collections.Generic;

namespace System.Collections.Generic
{
    public class PriorityQueue<TElement, TPriority>
    {
        (TElement Element, TPriority Priority)[] _nodes = new (TElement, TPriority)[4];
        int _size;
        readonly IComparer<TPriority> _cmp;
        const int Arity = 4, Log2Arity = 2;

        public PriorityQueue() : this((IComparer<TPriority>)null) { }
        public PriorityQueue(int initialCapacity) : this((IComparer<TPriority>)null) { }
        public PriorityQueue(IComparer<TPriority> comparer) { _cmp = comparer ?? Comparer<TPriority>.Default; }
        public PriorityQueue(IEnumerable<(TElement Element, TPriority Priority)> items) : this() { foreach (var (e, p) in items) Enqueue(e, p); }

        public int Count => _size;
        public IComparer<TPriority> Comparer => _cmp;

        public void Enqueue(TElement element, TPriority priority)
        {
            if (_size == _nodes.Length) Array.Resize(ref _nodes, _nodes.Length * 2);
            MoveUp((element, priority), _size++);
        }
        public TElement Peek()
        {
            if (_size == 0) throw new InvalidOperationException("Queue empty.");
            return _nodes[0].Element;
        }
        public bool TryPeek(out TElement element, out TPriority priority)
        {
            if (_size == 0) { element = default; priority = default; return false; }
            (element, priority) = _nodes[0];
            return true;
        }
        public TElement Dequeue()
        {
            if (_size == 0) throw new InvalidOperationException("Queue empty.");
            var e = _nodes[0].Element;
            RemoveRoot();
            return e;
        }
        public bool TryDequeue(out TElement element, out TPriority priority)
        {
            if (_size == 0) { element = default; priority = default; return false; }
            (element, priority) = _nodes[0];
            RemoveRoot();
            return true;
        }
        public TElement EnqueueDequeue(TElement element, TPriority priority)
        {
            if (_size != 0 && _cmp.Compare(priority, _nodes[0].Priority) > 0)
            {
                var root = _nodes[0];
                MoveDown((element, priority), 0);
                return root.Element;
            }
            return element;
        }
        public void Clear() { Array.Clear(_nodes, 0, _size); _size = 0; }
        public void EnqueueRange(IEnumerable<(TElement Element, TPriority Priority)> items) { foreach (var (e, p) in items) Enqueue(e, p); }

        void RemoveRoot()
        {
            int last = --_size;
            if (last > 0) MoveDown(_nodes[last], 0);
            _nodes[last] = default;
        }
        void MoveUp((TElement, TPriority) node, int index)
        {
            while (index > 0)
            {
                int parent = (index - 1) >> Log2Arity;
                var p = _nodes[parent];
                if (_cmp.Compare(node.Item2, p.Item2) < 0) { _nodes[index] = p; index = parent; }
                else break;
            }
            _nodes[index] = node;
        }
        void MoveDown((TElement, TPriority) node, int index)
        {
            int i;
            while ((i = (index << Log2Arity) + 1) < _size)
            {
                var min = _nodes[i]; int minIndex = i;
                int end = Math.Min(i + Arity, _size);
                while (++i < end) { var c = _nodes[i]; if (_cmp.Compare(c.Item2, min.Item2) < 0) { min = c; minIndex = i; } }
                if (_cmp.Compare(node.Item2, min.Item2) <= 0) break;
                _nodes[index] = min;
                index = minIndex;
            }
            _nodes[index] = node;
        }
    }

    public sealed class LinkedListNode<T>
    {
        internal LinkedList<T> _list;
        internal LinkedListNode<T> _next, _prev;
        T _value;
        public LinkedListNode(T value) { _value = value; }
        public LinkedList<T> List => _list;
        public LinkedListNode<T> Next => _next == null || _next == _list._head ? null : _next;
        public LinkedListNode<T> Previous => _prev == null || this == _list._head ? null : _prev;
        public T Value { get => _value; set => _value = value; }
        public ref T ValueRef => ref _value;
    }

    /// <summary>A circular doubly linked list, as .NET's (head's previous is the tail).</summary>
    public class LinkedList<T> : ICollection<T>, IReadOnlyCollection<T>
    {
        internal LinkedListNode<T> _head;
        int _count;
        public LinkedList() { }
        public LinkedList(IEnumerable<T> collection) { foreach (var x in collection) AddLast(x); }
        public int Count => _count;
        public bool IsReadOnly => false;
        public LinkedListNode<T> First => _head;
        public LinkedListNode<T> Last => _head?._prev;

        void InsertBefore(LinkedListNode<T> node, LinkedListNode<T> n)
        {
            n._list = this;
            n._next = node; n._prev = node._prev;
            node._prev._next = n; node._prev = n;
            _count++;
        }
        void InsertEmpty(LinkedListNode<T> n) { n._list = this; n._next = n; n._prev = n; _head = n; _count++; }
        void Check(LinkedListNode<T> n)
        {
            if (n == null) throw new ArgumentNullException("node");
            if (n._list != this) throw new InvalidOperationException("The LinkedList node does not belong to current LinkedList.");
        }

        public LinkedListNode<T> AddFirst(T value) { var n = new LinkedListNode<T>(value); AddFirst(n); return n; }
        public void AddFirst(LinkedListNode<T> n) { if (_head == null) InsertEmpty(n); else { InsertBefore(_head, n); _head = n; } }
        public LinkedListNode<T> AddLast(T value) { var n = new LinkedListNode<T>(value); AddLast(n); return n; }
        public void AddLast(LinkedListNode<T> n) { if (_head == null) InsertEmpty(n); else InsertBefore(_head, n); }
        public LinkedListNode<T> AddBefore(LinkedListNode<T> node, T value) { Check(node); var n = new LinkedListNode<T>(value); InsertBefore(node, n); if (node == _head) _head = n; return n; }
        public LinkedListNode<T> AddAfter(LinkedListNode<T> node, T value) { Check(node); var n = new LinkedListNode<T>(value); InsertBefore(node._next, n); return n; }
        void ICollection<T>.Add(T item) => AddLast(item);

        public LinkedListNode<T> Find(T value)
        {
            var n = _head;
            var c = EqualityComparer<T>.Default;
            if (n == null) return null;
            do { if (c.Equals(n.Value, value)) return n; n = n._next; } while (n != _head);
            return null;
        }
        public LinkedListNode<T> FindLast(T value)
        {
            if (_head == null) return null;
            var n = _head._prev;
            var c = EqualityComparer<T>.Default;
            do { if (c.Equals(n.Value, value)) return n; n = n._prev; } while (n != _head._prev);
            return null;
        }
        public bool Contains(T value) => Find(value) != null;
        public bool Remove(T value) { var n = Find(value); if (n == null) return false; Remove(n); return true; }
        public void Remove(LinkedListNode<T> node)
        {
            Check(node);
            if (node._next == node) _head = null;
            else
            {
                node._next._prev = node._prev; node._prev._next = node._next;
                if (_head == node) _head = node._next;
            }
            node._list = null; node._next = null; node._prev = null;
            _count--;
        }
        public void RemoveFirst() { if (_head == null) throw new InvalidOperationException("The LinkedList is empty."); Remove(_head); }
        public void RemoveLast() { if (_head == null) throw new InvalidOperationException("The LinkedList is empty."); Remove(_head._prev); }
        public void Clear()
        {
            var n = _head;
            while (n != null && _count > 0) { var next = n._next; n._list = null; n._next = null; n._prev = null; n = next; _count--; }
            _head = null; _count = 0;
        }
        public void CopyTo(T[] array, int index) { foreach (var x in this) array[index++] = x; }
        public Enumerator GetEnumerator() => new Enumerator(this);
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<T>
        {
            readonly LinkedList<T> _list; LinkedListNode<T> _node; T _current; int _index;
            internal Enumerator(LinkedList<T> l) { _list = l; _node = l._head; _current = default; _index = 0; }
            public bool MoveNext()
            {
                if (_node == null) { _index = _list.Count + 1; return false; }
                _index++;
                _current = _node.Value;
                _node = _node._next;
                if (_node == _list._head) _node = null;
                return true;
            }
            public T Current => _current;
            object IEnumerator.Current => _current;
            public void Reset() { _node = _list._head; _index = 0; }
            public void Dispose() { }
        }
    }

    public static class CollectionExtensions
    {
        public static TValue GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key) => dictionary.TryGetValue(key, out var v) ? v : default;
        public static TValue GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key, TValue defaultValue) => dictionary.TryGetValue(key, out var v) ? v : defaultValue;
        public static bool TryAdd<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, TValue value)
        {
            if (dictionary.ContainsKey(key)) return false;
            dictionary.Add(key, value);
            return true;
        }
        public static bool Remove<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, out TValue value)
        {
            if (dictionary.TryGetValue(key, out value)) { dictionary.Remove(key); return true; }
            return false;
        }
    }
}

namespace System.Linq
{
    public interface ILookup<TKey, TElement> : IEnumerable<IGrouping<TKey, TElement>>
    {
        int Count { get; }
        IEnumerable<TElement> this[TKey key] { get; }
        bool Contains(TKey key);
    }

    public class Lookup<TKey, TElement> : ILookup<TKey, TElement>
    {
        readonly Dictionary<TKey, Grouping<TKey, TElement>> _map;
        readonly List<Grouping<TKey, TElement>> _order = new List<Grouping<TKey, TElement>>();
        internal Lookup(IEqualityComparer<TKey> cmp) { _map = new Dictionary<TKey, Grouping<TKey, TElement>>(cmp); }
        internal void Add(TKey key, TElement e)
        {
            if (!_map.TryGetValue(key, out var g)) { g = new Grouping<TKey, TElement>(key); _map[key] = g; _order.Add(g); }
            g.Elements.Add(e);
        }
        public int Count => _order.Count;
        public IEnumerable<TElement> this[TKey key] => _map.TryGetValue(key, out var g) ? g : new TElement[0];
        public bool Contains(TKey key) => _map.ContainsKey(key);
        public IEnumerator<IGrouping<TKey, TElement>> GetEnumerator() { foreach (var g in _order) yield return g; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public static partial class EnumerableJoins
    {
    }
}

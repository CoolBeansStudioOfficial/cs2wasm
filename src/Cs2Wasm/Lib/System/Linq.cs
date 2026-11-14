// LINQ to Objects with .NET's deferred-execution semantics (hand-written iterators).
#pragma warning disable CS0436
using System.Collections;
using System.Collections.Generic;

namespace System.Linq
{
    internal abstract class Iterator<T> : IEnumerable<T>, IEnumerator<T>
    {
        protected int _state;
        protected T _current;
        public T Current => _current;
        object IEnumerator.Current => _current;
        public abstract Iterator<T> Clone();
        public abstract bool MoveNext();
        public virtual void Dispose() { _current = default; _state = -1; }
        public void Reset() => throw new NotSupportedException("Specified method is not supported.");
        public IEnumerator<T> GetEnumerator()
        {
            var e = _state == 0 && !_handedOut ? this : Clone();
            e._handedOut = true;
            e._state = 1;
            return e;
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        bool _handedOut;
    }

    sealed class WhereIterator<T> : Iterator<T>
    {
        readonly IEnumerable<T> _source; readonly Func<T, bool> _pred; IEnumerator<T> _e;
        public WhereIterator(IEnumerable<T> s, Func<T, bool> p) { _source = s; _pred = p; }
        public override Iterator<T> Clone() => new WhereIterator<T>(_source, _pred);
        public override bool MoveNext()
        {
            if (_state == 1) { _e = _source.GetEnumerator(); _state = 2; }
            if (_state == 2)
            {
                while (_e.MoveNext())
                {
                    T item = _e.Current;
                    if (_pred(item)) { _current = item; return true; }
                }
                Dispose();
            }
            return false;
        }
        public override void Dispose() { if (_e != null) { _e.Dispose(); _e = null; } base.Dispose(); }
    }

    sealed class WhereIndexIterator<T> : Iterator<T>
    {
        readonly IEnumerable<T> _source; readonly Func<T, int, bool> _pred; IEnumerator<T> _e; int _i;
        public WhereIndexIterator(IEnumerable<T> s, Func<T, int, bool> p) { _source = s; _pred = p; }
        public override Iterator<T> Clone() => new WhereIndexIterator<T>(_source, _pred);
        public override bool MoveNext()
        {
            if (_state == 1) { _e = _source.GetEnumerator(); _state = 2; _i = -1; }
            if (_state == 2)
            {
                while (_e.MoveNext()) { _i++; T item = _e.Current; if (_pred(item, _i)) { _current = item; return true; } }
                Dispose();
            }
            return false;
        }
        public override void Dispose() { if (_e != null) { _e.Dispose(); _e = null; } base.Dispose(); }
    }

    sealed class SelectIterator<TSource, TResult> : Iterator<TResult>
    {
        readonly IEnumerable<TSource> _source; readonly Func<TSource, TResult> _sel; IEnumerator<TSource> _e;
        public SelectIterator(IEnumerable<TSource> s, Func<TSource, TResult> f) { _source = s; _sel = f; }
        public override Iterator<TResult> Clone() => new SelectIterator<TSource, TResult>(_source, _sel);
        public override bool MoveNext()
        {
            if (_state == 1) { _e = _source.GetEnumerator(); _state = 2; }
            if (_state == 2)
            {
                if (_e.MoveNext()) { _current = _sel(_e.Current); return true; }
                Dispose();
            }
            return false;
        }
        public override void Dispose() { if (_e != null) { _e.Dispose(); _e = null; } base.Dispose(); }
    }

    sealed class SelectIndexIterator<TSource, TResult> : Iterator<TResult>
    {
        readonly IEnumerable<TSource> _source; readonly Func<TSource, int, TResult> _sel; IEnumerator<TSource> _e; int _i;
        public SelectIndexIterator(IEnumerable<TSource> s, Func<TSource, int, TResult> f) { _source = s; _sel = f; }
        public override Iterator<TResult> Clone() => new SelectIndexIterator<TSource, TResult>(_source, _sel);
        public override bool MoveNext()
        {
            if (_state == 1) { _e = _source.GetEnumerator(); _state = 2; _i = -1; }
            if (_state == 2)
            {
                if (_e.MoveNext()) { _i++; _current = _sel(_e.Current, _i); return true; }
                Dispose();
            }
            return false;
        }
        public override void Dispose() { if (_e != null) { _e.Dispose(); _e = null; } base.Dispose(); }
    }

    sealed class SelectManyIterator<TSource, TResult> : Iterator<TResult>
    {
        readonly IEnumerable<TSource> _source; readonly Func<TSource, IEnumerable<TResult>> _sel;
        IEnumerator<TSource> _e; IEnumerator<TResult> _inner;
        public SelectManyIterator(IEnumerable<TSource> s, Func<TSource, IEnumerable<TResult>> f) { _source = s; _sel = f; }
        public override Iterator<TResult> Clone() => new SelectManyIterator<TSource, TResult>(_source, _sel);
        public override bool MoveNext()
        {
            if (_state == 1) { _e = _source.GetEnumerator(); _state = 2; }
            while (_state == 2)
            {
                if (_inner != null)
                {
                    if (_inner.MoveNext()) { _current = _inner.Current; return true; }
                    _inner.Dispose(); _inner = null;
                }
                if (!_e.MoveNext()) { Dispose(); break; }
                _inner = _sel(_e.Current).GetEnumerator();
            }
            return false;
        }
        public override void Dispose()
        {
            if (_inner != null) { _inner.Dispose(); _inner = null; }
            if (_e != null) { _e.Dispose(); _e = null; }
            base.Dispose();
        }
    }

    sealed class TakeIterator<T> : Iterator<T>
    {
        readonly IEnumerable<T> _source; readonly int _count; IEnumerator<T> _e; int _left;
        public TakeIterator(IEnumerable<T> s, int n) { _source = s; _count = n; }
        public override Iterator<T> Clone() => new TakeIterator<T>(_source, _count);
        public override bool MoveNext()
        {
            if (_state == 1) { _left = _count; if (_left <= 0) { Dispose(); return false; } _e = _source.GetEnumerator(); _state = 2; }
            if (_state == 2)
            {
                if (_left > 0 && _e.MoveNext()) { _current = _e.Current; _left--; if (_left == 0) { } return true; }
                Dispose();
            }
            return false;
        }
        public override void Dispose() { if (_e != null) { _e.Dispose(); _e = null; } base.Dispose(); }
    }

    sealed class SkipIterator<T> : Iterator<T>
    {
        readonly IEnumerable<T> _source; readonly int _count; IEnumerator<T> _e;
        public SkipIterator(IEnumerable<T> s, int n) { _source = s; _count = n; }
        public override Iterator<T> Clone() => new SkipIterator<T>(_source, _count);
        public override bool MoveNext()
        {
            if (_state == 1)
            {
                _e = _source.GetEnumerator(); _state = 2;
                for (int i = 0; i < _count; i++) if (!_e.MoveNext()) { Dispose(); return false; }
            }
            if (_state == 2)
            {
                if (_e.MoveNext()) { _current = _e.Current; return true; }
                Dispose();
            }
            return false;
        }
        public override void Dispose() { if (_e != null) { _e.Dispose(); _e = null; } base.Dispose(); }
    }

    sealed class TakeWhileIterator<T> : Iterator<T>
    {
        readonly IEnumerable<T> _source; readonly Func<T, bool> _pred; IEnumerator<T> _e;
        public TakeWhileIterator(IEnumerable<T> s, Func<T, bool> p) { _source = s; _pred = p; }
        public override Iterator<T> Clone() => new TakeWhileIterator<T>(_source, _pred);
        public override bool MoveNext()
        {
            if (_state == 1) { _e = _source.GetEnumerator(); _state = 2; }
            if (_state == 2)
            {
                if (_e.MoveNext() && _pred(_e.Current)) { _current = _e.Current; return true; }
                Dispose();
            }
            return false;
        }
        public override void Dispose() { if (_e != null) { _e.Dispose(); _e = null; } base.Dispose(); }
    }

    sealed class SkipWhileIterator<T> : Iterator<T>
    {
        readonly IEnumerable<T> _source; readonly Func<T, bool> _pred; IEnumerator<T> _e; bool _yielding;
        public SkipWhileIterator(IEnumerable<T> s, Func<T, bool> p) { _source = s; _pred = p; }
        public override Iterator<T> Clone() => new SkipWhileIterator<T>(_source, _pred);
        public override bool MoveNext()
        {
            if (_state == 1) { _e = _source.GetEnumerator(); _state = 2; _yielding = false; }
            if (_state == 2)
            {
                while (_e.MoveNext())
                {
                    T item = _e.Current;
                    if (!_yielding && _pred(item)) continue;
                    _yielding = true;
                    _current = item;
                    return true;
                }
                Dispose();
            }
            return false;
        }
        public override void Dispose() { if (_e != null) { _e.Dispose(); _e = null; } base.Dispose(); }
    }

    sealed class ConcatIterator<T> : Iterator<T>
    {
        readonly IEnumerable<T> _a, _b; IEnumerator<T> _e;
        public ConcatIterator(IEnumerable<T> a, IEnumerable<T> b) { _a = a; _b = b; }
        public override Iterator<T> Clone() => new ConcatIterator<T>(_a, _b);
        public override bool MoveNext()
        {
            if (_state == 1) { _e = _a.GetEnumerator(); _state = 2; }
            if (_state == 2)
            {
                if (_e.MoveNext()) { _current = _e.Current; return true; }
                _e.Dispose(); _e = _b.GetEnumerator(); _state = 3;
            }
            if (_state == 3)
            {
                if (_e.MoveNext()) { _current = _e.Current; return true; }
                Dispose();
            }
            return false;
        }
        public override void Dispose() { if (_e != null) { _e.Dispose(); _e = null; } base.Dispose(); }
    }

    sealed class RangeIterator : Iterator<int>
    {
        readonly int _start, _end; int _next;
        public RangeIterator(int start, int count) { _start = start; _end = start + count; }
        public override Iterator<int> Clone() => new RangeIterator(_start, _end - _start);
        public override bool MoveNext()
        {
            if (_state == 1) { _next = _start; _state = 2; }
            if (_state == 2)
            {
                if (_next != _end) { _current = _next++; return true; }
                Dispose();
            }
            return false;
        }
    }

    sealed class RepeatIterator<T> : Iterator<T>
    {
        readonly T _value; readonly int _count; int _i;
        public RepeatIterator(T v, int n) { _value = v; _count = n; }
        public override Iterator<T> Clone() => new RepeatIterator<T>(_value, _count);
        public override bool MoveNext()
        {
            if (_state == 1) { _i = 0; _state = 2; }
            if (_state == 2)
            {
                if (_i < _count) { _i++; _current = _value; return true; }
                Dispose();
            }
            return false;
        }
    }

    /// <summary>Buffers the source on first MoveNext, then yields from the buffer (Reverse, OrderBy, Distinct...).</summary>
    sealed class BufferIterator<T> : Iterator<T>
    {
        readonly Func<List<T>> _produce; List<T> _buf; int _i;
        public BufferIterator(Func<List<T>> produce) { _produce = produce; }
        public override Iterator<T> Clone() => new BufferIterator<T>(_produce);
        public override bool MoveNext()
        {
            if (_state == 1) { _buf = _produce(); _i = 0; _state = 2; }
            if (_state == 2)
            {
                if (_i < _buf.Count) { _current = _buf[_i++]; return true; }
                Dispose();
            }
            return false;
        }
        public override void Dispose() { _buf = null; base.Dispose(); }
    }

    sealed class DistinctIterator<T> : Iterator<T>
    {
        readonly IEnumerable<T> _source; readonly IEqualityComparer<T> _cmp; IEnumerator<T> _e; HashSet<T> _seen;
        public DistinctIterator(IEnumerable<T> s, IEqualityComparer<T> c) { _source = s; _cmp = c; }
        public override Iterator<T> Clone() => new DistinctIterator<T>(_source, _cmp);
        public override bool MoveNext()
        {
            if (_state == 1) { _e = _source.GetEnumerator(); _seen = new HashSet<T>(_cmp); _state = 2; }
            if (_state == 2)
            {
                while (_e.MoveNext()) { T x = _e.Current; if (_seen.Add(x)) { _current = x; return true; } }
                Dispose();
            }
            return false;
        }
        public override void Dispose() { if (_e != null) { _e.Dispose(); _e = null; } _seen = null; base.Dispose(); }
    }

    sealed class ZipIterator<TFirst, TSecond, TResult> : Iterator<TResult>
    {
        readonly IEnumerable<TFirst> _a; readonly IEnumerable<TSecond> _b; readonly Func<TFirst, TSecond, TResult> _f;
        IEnumerator<TFirst> _ea; IEnumerator<TSecond> _eb;
        public ZipIterator(IEnumerable<TFirst> a, IEnumerable<TSecond> b, Func<TFirst, TSecond, TResult> f) { _a = a; _b = b; _f = f; }
        public override Iterator<TResult> Clone() => new ZipIterator<TFirst, TSecond, TResult>(_a, _b, _f);
        public override bool MoveNext()
        {
            if (_state == 1) { _ea = _a.GetEnumerator(); _eb = _b.GetEnumerator(); _state = 2; }
            if (_state == 2)
            {
                if (_ea.MoveNext() && _eb.MoveNext()) { _current = _f(_ea.Current, _eb.Current); return true; }
                Dispose();
            }
            return false;
        }
        public override void Dispose()
        {
            if (_ea != null) { _ea.Dispose(); _ea = null; }
            if (_eb != null) { _eb.Dispose(); _eb = null; }
            base.Dispose();
        }
    }

    sealed class CastIterator<TResult> : Iterator<TResult>
    {
        readonly IEnumerable _source; readonly bool _filter; IEnumerator _e;
        public CastIterator(IEnumerable s, bool filter) { _source = s; _filter = filter; }
        public override Iterator<TResult> Clone() => new CastIterator<TResult>(_source, _filter);
        public override bool MoveNext()
        {
            if (_state == 1) { _e = _source.GetEnumerator(); _state = 2; }
            if (_state == 2)
            {
                while (_e.MoveNext())
                {
                    object o = _e.Current;
                    if (_filter) { if (o is TResult r) { _current = r; return true; } continue; }
                    _current = (TResult)o;
                    return true;
                }
                Dispose();
            }
            return false;
        }
        public override void Dispose() { if (_e is IDisposable d) d.Dispose(); _e = null; base.Dispose(); }
    }

    public interface IOrderedEnumerable<out TElement> : IEnumerable<TElement> { }

    sealed class OrderedEnumerable<T> : IOrderedEnumerable<T>
    {
        internal readonly IEnumerable<T> Source;
        internal readonly List<Comparison<T>> Keys;
        public OrderedEnumerable(IEnumerable<T> source, List<Comparison<T>> keys) { Source = source; Keys = keys; }
        public IEnumerator<T> GetEnumerator()
        {
            var keys = Keys;
            var src = Source;
            return new BufferIterator<T>(() =>
            {
                var items = new List<T>(src);
                int n = items.Count;
                var idx = new int[n];
                for (int i = 0; i < n; i++) idx[i] = i;
                var arr = items._items;
                // stable: ties broken by original position
                ArraySortHelper<int>.Sort(idx, 0, n, (a, b) =>
                {
                    for (int k = 0; k < keys.Count; k++)
                    {
                        int c = keys[k](arr[a], arr[b]);
                        if (c != 0) return c;
                    }
                    return a - b;
                });
                var result = new List<T>(n);
                for (int i = 0; i < n; i++) result.Add(arr[idx[i]]);
                return result;
            }).GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public interface IGrouping<out TKey, out TElement> : IEnumerable<TElement> { TKey Key { get; } }

    sealed class Grouping<TKey, TElement> : IGrouping<TKey, TElement>
    {
        internal readonly List<TElement> Elements = new List<TElement>();
        public TKey Key { get; }
        public Grouping(TKey key) { Key = key; }
        public IEnumerator<TElement> GetEnumerator() => ((IEnumerable<TElement>)Elements).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public static class Enumerable
    {
        static void Null(object o, string name) { if (o == null) throw new ArgumentNullException(name); }
        static InvalidOperationException NoElements() => new InvalidOperationException("Sequence contains no elements");
        static InvalidOperationException NoMatch() => new InvalidOperationException("Sequence contains no matching element");
        static InvalidOperationException MoreThanOne() => new InvalidOperationException("Sequence contains more than one element");
        static InvalidOperationException MoreThanOneMatch() => new InvalidOperationException("Sequence contains more than one matching element");

        public static IEnumerable<T> Where<T>(this IEnumerable<T> source, Func<T, bool> predicate) { Null(source, "source"); Null(predicate, "predicate"); return new WhereIterator<T>(source, predicate); }
        public static IEnumerable<T> Where<T>(this IEnumerable<T> source, Func<T, int, bool> predicate) { Null(source, "source"); Null(predicate, "predicate"); return new WhereIndexIterator<T>(source, predicate); }
        public static IEnumerable<TResult> Select<TSource, TResult>(this IEnumerable<TSource> source, Func<TSource, TResult> selector) { Null(source, "source"); Null(selector, "selector"); return new SelectIterator<TSource, TResult>(source, selector); }
        public static IEnumerable<TResult> Select<TSource, TResult>(this IEnumerable<TSource> source, Func<TSource, int, TResult> selector) { Null(source, "source"); Null(selector, "selector"); return new SelectIndexIterator<TSource, TResult>(source, selector); }
        public static IEnumerable<TResult> SelectMany<TSource, TResult>(this IEnumerable<TSource> source, Func<TSource, IEnumerable<TResult>> selector) { Null(source, "source"); Null(selector, "selector"); return new SelectManyIterator<TSource, TResult>(source, selector); }
        public static IEnumerable<T> Take<T>(this IEnumerable<T> source, int count) { Null(source, "source"); return new TakeIterator<T>(source, count); }
        public static IEnumerable<T[]> Chunk<T>(this IEnumerable<T> source, int size)
        {
            Null(source, "source");
            if (size < 1) throw new ArgumentOutOfRangeException("size");
            return ChunkIterator(source, size);
        }
        static IEnumerable<T[]> ChunkIterator<T>(IEnumerable<T> source, int size)
        {
            var buf = new List<T>(size);
            foreach (var x in source)
            {
                buf.Add(x);
                if (buf.Count == size) { yield return buf.ToArray(); buf.Clear(); }
            }
            if (buf.Count > 0) yield return buf.ToArray();
        }
        public static IEnumerable<T> Skip<T>(this IEnumerable<T> source, int count) { Null(source, "source"); return new SkipIterator<T>(source, count < 0 ? 0 : count); }
        public static IEnumerable<T> TakeWhile<T>(this IEnumerable<T> source, Func<T, bool> predicate) { Null(source, "source"); return new TakeWhileIterator<T>(source, predicate); }
        public static IEnumerable<T> SkipWhile<T>(this IEnumerable<T> source, Func<T, bool> predicate) { Null(source, "source"); return new SkipWhileIterator<T>(source, predicate); }
        public static IEnumerable<T> Concat<T>(this IEnumerable<T> first, IEnumerable<T> second) { Null(first, "first"); Null(second, "second"); return new ConcatIterator<T>(first, second); }
        public static IEnumerable<T> Append<T>(this IEnumerable<T> source, T element) => new ConcatIterator<T>(source, new RepeatIterator<T>(element, 1));
        public static IEnumerable<T> Prepend<T>(this IEnumerable<T> source, T element) => new ConcatIterator<T>(new RepeatIterator<T>(element, 1), source);
        public static IEnumerable<int> Range(int start, int count)
        {
            if (count < 0 || (long)start + count - 1 > int.MaxValue) throw new ArgumentOutOfRangeException("count");
            return new RangeIterator(start, count);
        }
        public static IEnumerable<T> Repeat<T>(T element, int count) { if (count < 0) throw new ArgumentOutOfRangeException("count"); return new RepeatIterator<T>(element, count); }
        public static IEnumerable<T> Empty<T>() => Array.Empty<T>();
        public static IEnumerable<T> Reverse<T>(this IEnumerable<T> source)
        {
            Null(source, "source");
            return new BufferIterator<T>(() => { var l = new List<T>(source); l.Reverse(); return l; });
        }
        public static IEnumerable<T> Distinct<T>(this IEnumerable<T> source) => new DistinctIterator<T>(source, null);
        public static IEnumerable<T> Distinct<T>(this IEnumerable<T> source, IEqualityComparer<T> comparer) => new DistinctIterator<T>(source, comparer);
        public static IEnumerable<T> DistinctBy<T, TKey>(this IEnumerable<T> source, Func<T, TKey> keySelector)
        {
            return new BufferIterator<T>(() =>
            {
                var seen = new HashSet<TKey>(); var l = new List<T>();
                foreach (var x in source) if (seen.Add(keySelector(x))) l.Add(x);
                return l;
            });
        }
        public static IEnumerable<T> Union<T>(this IEnumerable<T> first, IEnumerable<T> second) => new DistinctIterator<T>(new ConcatIterator<T>(first, second), null);
        public static IEnumerable<T> Intersect<T>(this IEnumerable<T> first, IEnumerable<T> second)
        {
            return new BufferIterator<T>(() =>
            {
                var set = new HashSet<T>(second); var l = new List<T>();
                foreach (var x in first) if (set.Remove(x)) l.Add(x);
                return l;
            });
        }
        public static IEnumerable<T> Except<T>(this IEnumerable<T> first, IEnumerable<T> second)
        {
            return new BufferIterator<T>(() =>
            {
                var set = new HashSet<T>(second); var l = new List<T>();
                foreach (var x in first) if (set.Add(x)) l.Add(x);
                return l;
            });
        }
        public static IEnumerable<TResult> Zip<TFirst, TSecond, TResult>(this IEnumerable<TFirst> first, IEnumerable<TSecond> second, Func<TFirst, TSecond, TResult> resultSelector) => new ZipIterator<TFirst, TSecond, TResult>(first, second, resultSelector);
        public static IEnumerable<TResult> Cast<TResult>(this IEnumerable source) { Null(source, "source"); if (source is IEnumerable<TResult> t) return t; return new CastIterator<TResult>(source, false); }
        public static IEnumerable<TResult> OfType<TResult>(this IEnumerable source) { Null(source, "source"); return new CastIterator<TResult>(source, true); }
        public static IEnumerable<T> DefaultIfEmpty<T>(this IEnumerable<T> source) => DefaultIfEmpty(source, default);
        public static IEnumerable<T> DefaultIfEmpty<T>(this IEnumerable<T> source, T defaultValue)
        {
            return new BufferIterator<T>(() => { var l = new List<T>(source); if (l.Count == 0) l.Add(defaultValue); return l; });
        }
        public static IEnumerable<T> AsEnumerable<T>(this IEnumerable<T> source) => source;

        public static IOrderedEnumerable<T> OrderBy<T, TKey>(this IEnumerable<T> source, Func<T, TKey> keySelector) =>
            new OrderedEnumerable<T>(source, new List<Comparison<T>> { (a, b) => Comparer<TKey>.Default.Compare(keySelector(a), keySelector(b)) });
        public static IOrderedEnumerable<T> OrderBy<T, TKey>(this IEnumerable<T> source, Func<T, TKey> keySelector, IComparer<TKey> comparer) =>
            new OrderedEnumerable<T>(source, new List<Comparison<T>> { (a, b) => comparer.Compare(keySelector(a), keySelector(b)) });
        public static IOrderedEnumerable<T> OrderByDescending<T, TKey>(this IEnumerable<T> source, Func<T, TKey> keySelector) =>
            new OrderedEnumerable<T>(source, new List<Comparison<T>> { (a, b) => Comparer<TKey>.Default.Compare(keySelector(b), keySelector(a)) });
        public static IOrderedEnumerable<T> Order<T>(this IEnumerable<T> source) => OrderBy(source, x => x);
        public static IOrderedEnumerable<T> OrderDescending<T>(this IEnumerable<T> source) => OrderByDescending(source, x => x);
        public static IOrderedEnumerable<T> ThenBy<T, TKey>(this IOrderedEnumerable<T> source, Func<T, TKey> keySelector)
        {
            var o = (OrderedEnumerable<T>)source;
            var keys = new List<Comparison<T>>(o.Keys) { (a, b) => Comparer<TKey>.Default.Compare(keySelector(a), keySelector(b)) };
            return new OrderedEnumerable<T>(o.Source, keys);
        }
        public static IOrderedEnumerable<T> ThenByDescending<T, TKey>(this IOrderedEnumerable<T> source, Func<T, TKey> keySelector)
        {
            var o = (OrderedEnumerable<T>)source;
            var keys = new List<Comparison<T>>(o.Keys) { (a, b) => Comparer<TKey>.Default.Compare(keySelector(b), keySelector(a)) };
            return new OrderedEnumerable<T>(o.Source, keys);
        }

        public static IEnumerable<IGrouping<TKey, T>> GroupBy<T, TKey>(this IEnumerable<T> source, Func<T, TKey> keySelector)
        {
            return new BufferIterator<IGrouping<TKey, T>>(() =>
            {
                var map = new Dictionary<TKey, Grouping<TKey, T>>();
                var order = new List<IGrouping<TKey, T>>();
                Grouping<TKey, T> nullGroup = null;
                foreach (var x in source)
                {
                    var k = keySelector(x);
                    Grouping<TKey, T> g;
                    if (k == null) { if (nullGroup == null) { nullGroup = new Grouping<TKey, T>(k); order.Add(nullGroup); } g = nullGroup; }
                    else if (!map.TryGetValue(k, out g)) { g = new Grouping<TKey, T>(k); map.Add(k, g); order.Add(g); }
                    g.Elements.Add(x);
                }
                return order;
            });
        }
        public static IEnumerable<TResult> GroupBy<T, TKey, TResult>(this IEnumerable<T> source, Func<T, TKey> keySelector, Func<TKey, IEnumerable<T>, TResult> resultSelector) =>
            GroupBy(source, keySelector).Select(g => resultSelector(g.Key, g));
        public static IEnumerable<IGrouping<TKey, TElement>> GroupBy<T, TKey, TElement>(this IEnumerable<T> source, Func<T, TKey> keySelector, Func<T, TElement> elementSelector)
        {
            return new BufferIterator<IGrouping<TKey, TElement>>(() =>
            {
                var map = new Dictionary<TKey, Grouping<TKey, TElement>>();
                var order = new List<IGrouping<TKey, TElement>>();
                foreach (var x in source)
                {
                    var k = keySelector(x);
                    if (!map.TryGetValue(k, out var g)) { g = new Grouping<TKey, TElement>(k); map.Add(k, g); order.Add(g); }
                    g.Elements.Add(elementSelector(x));
                }
                return order;
            });
        }

        // ---- immediate ----
        public static List<T> ToList<T>(this IEnumerable<T> source) { Null(source, "source"); return new List<T>(source); }
        public static T[] ToArray<T>(this IEnumerable<T> source) { Null(source, "source"); return new List<T>(source).ToArray(); }
        public static HashSet<T> ToHashSet<T>(this IEnumerable<T> source) => new HashSet<T>(source);
        public static Dictionary<TKey, T> ToDictionary<T, TKey>(this IEnumerable<T> source, Func<T, TKey> keySelector)
        {
            var d = new Dictionary<TKey, T>();
            foreach (var x in source) d.Add(keySelector(x), x);
            return d;
        }
        public static Dictionary<TKey, TElement> ToDictionary<T, TKey, TElement>(this IEnumerable<T> source, Func<T, TKey> keySelector, Func<T, TElement> elementSelector)
        {
            var d = new Dictionary<TKey, TElement>();
            foreach (var x in source) d.Add(keySelector(x), elementSelector(x));
            return d;
        }
        public static Dictionary<TKey, TValue> ToDictionary<TKey, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> source)
        {
            var d = new Dictionary<TKey, TValue>();
            foreach (var x in source) d.Add(x.Key, x.Value);
            return d;
        }

        public static int Count<T>(this IEnumerable<T> source)
        {
            Null(source, "source");
            if (source is ICollection<T> c) return c.Count;
            int n = 0;
            using (var e = source.GetEnumerator()) while (e.MoveNext()) checked { n++; }
            return n;
        }
        public static int Count<T>(this IEnumerable<T> source, Func<T, bool> predicate)
        {
            int n = 0;
            foreach (var x in source) if (predicate(x)) checked { n++; }
            return n;
        }
        public static long LongCount<T>(this IEnumerable<T> source) { long n = 0; foreach (var x in source) n++; return n; }
        public static bool Any<T>(this IEnumerable<T> source)
        {
            Null(source, "source");
            if (source is ICollection<T> c) return c.Count != 0;
            using (var e = source.GetEnumerator()) return e.MoveNext();
        }
        public static bool Any<T>(this IEnumerable<T> source, Func<T, bool> predicate)
        {
            Null(source, "source"); Null(predicate, "predicate");
            foreach (var x in source) if (predicate(x)) return true;
            return false;
        }
        public static bool All<T>(this IEnumerable<T> source, Func<T, bool> predicate)
        {
            Null(source, "source"); Null(predicate, "predicate");
            foreach (var x in source) if (!predicate(x)) return false;
            return true;
        }
        public static bool Contains<T>(this IEnumerable<T> source, T value)
        {
            if (source is ICollection<T> c) return c.Contains(value);
            foreach (var x in source) if (EqualityComparer<T>.Default.Equals(x, value)) return true;
            return false;
        }
        public static bool SequenceEqual<T>(this IEnumerable<T> first, IEnumerable<T> second)
        {
            using (var a = first.GetEnumerator())
            using (var b = second.GetEnumerator())
            {
                while (a.MoveNext())
                    if (!b.MoveNext() || !EqualityComparer<T>.Default.Equals(a.Current, b.Current)) return false;
                return !b.MoveNext();
            }
        }

        public static T First<T>(this IEnumerable<T> source)
        {
            Null(source, "source");
            if (source is IList<T> l) { if (l.Count > 0) return l[0]; throw NoElements(); }
            using (var e = source.GetEnumerator()) if (e.MoveNext()) return e.Current;
            throw NoElements();
        }
        public static T First<T>(this IEnumerable<T> source, Func<T, bool> predicate)
        {
            foreach (var x in source) if (predicate(x)) return x;
            throw NoMatch();
        }
        public static T FirstOrDefault<T>(this IEnumerable<T> source)
        {
            Null(source, "source");
            if (source is IList<T> l) return l.Count > 0 ? l[0] : default;
            using (var e = source.GetEnumerator()) if (e.MoveNext()) return e.Current;
            return default;
        }
        public static T FirstOrDefault<T>(this IEnumerable<T> source, T defaultValue)
        {
            using (var e = source.GetEnumerator()) if (e.MoveNext()) return e.Current;
            return defaultValue;
        }
        public static T FirstOrDefault<T>(this IEnumerable<T> source, Func<T, bool> predicate)
        {
            foreach (var x in source) if (predicate(x)) return x;
            return default;
        }
        public static T Last<T>(this IEnumerable<T> source)
        {
            Null(source, "source");
            if (source is IList<T> l) { if (l.Count > 0) return l[l.Count - 1]; throw NoElements(); }
            bool found = false; T last = default;
            foreach (var x in source) { last = x; found = true; }
            if (!found) throw NoElements();
            return last;
        }
        public static T Last<T>(this IEnumerable<T> source, Func<T, bool> predicate)
        {
            bool found = false; T last = default;
            foreach (var x in source) if (predicate(x)) { last = x; found = true; }
            if (!found) throw NoMatch();
            return last;
        }
        public static T LastOrDefault<T>(this IEnumerable<T> source)
        {
            T last = default;
            foreach (var x in source) last = x;
            return last;
        }
        public static T LastOrDefault<T>(this IEnumerable<T> source, Func<T, bool> predicate)
        {
            T last = default;
            foreach (var x in source) if (predicate(x)) last = x;
            return last;
        }
        public static T Single<T>(this IEnumerable<T> source)
        {
            using (var e = source.GetEnumerator())
            {
                if (!e.MoveNext()) throw NoElements();
                T r = e.Current;
                if (!e.MoveNext()) return r;
            }
            throw MoreThanOne();
        }
        public static T Single<T>(this IEnumerable<T> source, Func<T, bool> predicate)
        {
            T r = default; long n = 0;
            foreach (var x in source) if (predicate(x)) { r = x; n++; }
            if (n == 0) throw NoMatch();
            if (n > 1) throw MoreThanOneMatch();
            return r;
        }
        public static T SingleOrDefault<T>(this IEnumerable<T> source)
        {
            using (var e = source.GetEnumerator())
            {
                if (!e.MoveNext()) return default;
                T r = e.Current;
                if (!e.MoveNext()) return r;
            }
            throw MoreThanOne();
        }
        public static T SingleOrDefault<T>(this IEnumerable<T> source, Func<T, bool> predicate)
        {
            T r = default; long n = 0;
            foreach (var x in source) if (predicate(x)) { r = x; n++; }
            if (n > 1) throw MoreThanOneMatch();
            return r;
        }
        public static T ElementAt<T>(this IEnumerable<T> source, int index)
        {
            if (source is IList<T> l) return l[index];
            if (index >= 0) foreach (var x in source) { if (index == 0) return x; index--; }
            throw new ArgumentOutOfRangeException("index", "Index was out of range. Must be non-negative and less than the size of the collection.");
        }
        public static T ElementAtOrDefault<T>(this IEnumerable<T> source, int index)
        {
            if (index >= 0) foreach (var x in source) { if (index == 0) return x; index--; }
            return default;
        }

        public static TAccumulate Aggregate<TSource, TAccumulate>(this IEnumerable<TSource> source, TAccumulate seed, Func<TAccumulate, TSource, TAccumulate> func)
        {
            TAccumulate acc = seed;
            foreach (var x in source) acc = func(acc, x);
            return acc;
        }
        public static TResult Aggregate<TSource, TAccumulate, TResult>(this IEnumerable<TSource> source, TAccumulate seed, Func<TAccumulate, TSource, TAccumulate> func, Func<TAccumulate, TResult> resultSelector)
        {
            TAccumulate acc = seed;
            foreach (var x in source) acc = func(acc, x);
            return resultSelector(acc);
        }
        public static T Aggregate<T>(this IEnumerable<T> source, Func<T, T, T> func)
        {
            using (var e = source.GetEnumerator())
            {
                if (!e.MoveNext()) throw NoElements();
                T acc = e.Current;
                while (e.MoveNext()) acc = func(acc, e.Current);
                return acc;
            }
        }

        public static int Sum(this IEnumerable<int> source) { int s = 0; foreach (var x in source) checked { s += x; } return s; }
        public static long Sum(this IEnumerable<long> source) { long s = 0; foreach (var x in source) checked { s += x; } return s; }
        public static double Sum(this IEnumerable<double> source) { double s = 0; foreach (var x in source) s += x; return s; }
        public static float Sum(this IEnumerable<float> source) { double s = 0; foreach (var x in source) s += x; return (float)s; }
        public static int Sum<T>(this IEnumerable<T> source, Func<T, int> selector) { int s = 0; foreach (var x in source) checked { s += selector(x); } return s; }
        public static long Sum<T>(this IEnumerable<T> source, Func<T, long> selector) { long s = 0; foreach (var x in source) checked { s += selector(x); } return s; }
        public static double Sum<T>(this IEnumerable<T> source, Func<T, double> selector) { double s = 0; foreach (var x in source) s += selector(x); return s; }
        public static float Sum<T>(this IEnumerable<T> source, Func<T, float> selector) { double s = 0; foreach (var x in source) s += selector(x); return (float)s; }

        public static double Average(this IEnumerable<int> source)
        {
            long s = 0; long n = 0;
            foreach (var x in source) { checked { s += x; } n++; }
            if (n == 0) throw NoElements();
            return (double)s / n;
        }
        public static double Average(this IEnumerable<long> source)
        {
            long s = 0; long n = 0;
            foreach (var x in source) { checked { s += x; } n++; }
            if (n == 0) throw NoElements();
            return (double)s / n;
        }
        public static double Average(this IEnumerable<double> source)
        {
            double s = 0; long n = 0;
            foreach (var x in source) { s += x; n++; }
            if (n == 0) throw NoElements();
            return s / n;
        }
        public static decimal Sum(this IEnumerable<decimal> source) { decimal s = 0; foreach (var x in source) s += x; return s; }
        public static decimal Sum<T>(this IEnumerable<T> source, Func<T, decimal> selector) => Sum(Select(source, selector));
        public static decimal Average(this IEnumerable<decimal> source)
        {
            decimal s = 0; long n = 0;
            foreach (var x in source) { s += x; n++; }
            if (n == 0) throw NoElements();
            return s / n;
        }
        public static decimal Average<T>(this IEnumerable<T> source, Func<T, decimal> selector) => Average(Select(source, selector));
        public static decimal Max(this IEnumerable<decimal> source)
        {
            using (var e = source.GetEnumerator()) { if (!e.MoveNext()) throw NoElements(); var m = e.Current; while (e.MoveNext()) if (e.Current > m) m = e.Current; return m; }
        }
        public static decimal Min(this IEnumerable<decimal> source)
        {
            using (var e = source.GetEnumerator()) { if (!e.MoveNext()) throw NoElements(); var m = e.Current; while (e.MoveNext()) if (e.Current < m) m = e.Current; return m; }
        }
        public static double Average<T>(this IEnumerable<T> source, Func<T, int> selector) => Average(Select(source, selector));
        public static double Average<T>(this IEnumerable<T> source, Func<T, double> selector) => Average(Select(source, selector));

        public static int Max(this IEnumerable<int> source)
        {
            using (var e = source.GetEnumerator())
            {
                if (!e.MoveNext()) throw NoElements();
                int m = e.Current;
                while (e.MoveNext()) if (e.Current > m) m = e.Current;
                return m;
            }
        }
        public static int Min(this IEnumerable<int> source)
        {
            using (var e = source.GetEnumerator())
            {
                if (!e.MoveNext()) throw NoElements();
                int m = e.Current;
                while (e.MoveNext()) if (e.Current < m) m = e.Current;
                return m;
            }
        }
        public static long Max(this IEnumerable<long> source)
        {
            using (var e = source.GetEnumerator())
            {
                if (!e.MoveNext()) throw NoElements();
                long m = e.Current;
                while (e.MoveNext()) if (e.Current > m) m = e.Current;
                return m;
            }
        }
        public static long Min(this IEnumerable<long> source)
        {
            using (var e = source.GetEnumerator())
            {
                if (!e.MoveNext()) throw NoElements();
                long m = e.Current;
                while (e.MoveNext()) if (e.Current < m) m = e.Current;
                return m;
            }
        }
        public static double Max(this IEnumerable<double> source)
        {
            using (var e = source.GetEnumerator())
            {
                if (!e.MoveNext()) throw NoElements();
                double m = e.Current;
                while (double.IsNaN(m)) { if (!e.MoveNext()) return m; m = e.Current; }
                while (e.MoveNext()) { double x = e.Current; if (x > m) m = x; }
                return m;
            }
        }
        public static double Min(this IEnumerable<double> source)
        {
            using (var e = source.GetEnumerator())
            {
                if (!e.MoveNext()) throw NoElements();
                double m = e.Current;
                if (double.IsNaN(m)) return m;
                while (e.MoveNext()) { double x = e.Current; if (x < m) m = x; else if (double.IsNaN(x)) return x; }
                return m;
            }
        }
        public static int Max<T>(this IEnumerable<T> source, Func<T, int> selector) => Max(Select(source, selector));
        public static int Min<T>(this IEnumerable<T> source, Func<T, int> selector) => Min(Select(source, selector));
        public static double Max<T>(this IEnumerable<T> source, Func<T, double> selector) => Max(Select(source, selector));
        public static double Min<T>(this IEnumerable<T> source, Func<T, double> selector) => Min(Select(source, selector));
        public static T Max<T>(this IEnumerable<T> source)
        {
            var c = Comparer<T>.Default;
            using (var e = source.GetEnumerator())
            {
                if (!e.MoveNext()) { if (default(T) == null) return default; throw NoElements(); }
                T m = e.Current;
                while (e.MoveNext()) if (c.Compare(e.Current, m) > 0) m = e.Current;
                return m;
            }
        }
        public static T Min<T>(this IEnumerable<T> source)
        {
            var c = Comparer<T>.Default;
            using (var e = source.GetEnumerator())
            {
                if (!e.MoveNext()) { if (default(T) == null) return default; throw NoElements(); }
                T m = e.Current;
                while (e.MoveNext()) if (c.Compare(e.Current, m) < 0) m = e.Current;
                return m;
            }
        }
        public static T MaxBy<T, TKey>(this IEnumerable<T> source, Func<T, TKey> keySelector)
        {
            var c = Comparer<TKey>.Default;
            using (var e = source.GetEnumerator())
            {
                if (!e.MoveNext()) { if (default(T) == null) return default; throw NoElements(); }
                T m = e.Current; TKey mk = keySelector(m);
                while (e.MoveNext()) { var k = keySelector(e.Current); if (c.Compare(k, mk) > 0) { m = e.Current; mk = k; } }
                return m;
            }
        }
        public static T MinBy<T, TKey>(this IEnumerable<T> source, Func<T, TKey> keySelector)
        {
            var c = Comparer<TKey>.Default;
            using (var e = source.GetEnumerator())
            {
                if (!e.MoveNext()) { if (default(T) == null) return default; throw NoElements(); }
                T m = e.Current; TKey mk = keySelector(m);
                while (e.MoveNext()) { var k = keySelector(e.Current); if (c.Compare(k, mk) < 0) { m = e.Current; mk = k; } }
                return m;
            }
        }
    }
}

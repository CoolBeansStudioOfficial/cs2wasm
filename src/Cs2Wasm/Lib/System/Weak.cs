#pragma warning disable CS0436
// Weak references (section 4.4). An object a weak reference or ConditionalWeakTable points at carries a mark; when
// its count reaches zero, the destroy path (rt.on_destroy) clears the handles pointing at it before its finalizer
// runs, as .NET's short weak references are cleared. GC.SuppressFinalize is a mark too.
using System.Collections;
using System.Collections.Generic;
using Cs2Wasm.Runtime;

namespace Cs2Wasm.Runtime
{
    /// <summary>Per-object flags kept beside the heap, by address. The destroy path only looks when any exist.</summary>
    public static class ObjectMarks
    {
        public const int Weak = 1, FinalizerSuppressed = 2;
        static Dictionary<int, int> s_marks;
        static int s_count, s_busy;

        public static void Set(object o, int flag)
        {
            Rt.RuntimeLock();
            s_busy++;
            s_marks ??= new Dictionary<int, int>();
            int a = Rt.AddressOf(o);
            s_marks.TryGetValue(a, out int f);
            if (f == 0) s_count++;
            s_marks[a] = f | flag;
            s_busy--;
            Rt.RuntimeUnlock();
        }

        public static void Clear(object o, int flag) => ClearAt(Rt.AddressOf(o), flag);

        internal static void ClearAt(int a, int flag)
        {
            Rt.RuntimeLock();
            s_busy++;
            if (s_marks != null && s_marks.TryGetValue(a, out int f))
            {
                f &= ~flag;
                if (f == 0) { s_marks.Remove(a); s_count--; } else s_marks[a] = f;
            }
            s_busy--;
            Rt.RuntimeUnlock();
        }

        /// <summary>The object at `addr` is dying: clear weak handles to it; 0 if its finalizer must not run.</summary>
        public static int OnDestroy(int addr)
        {
            if (s_count == 0 || s_busy != 0) return 1;
            Rt.RuntimeLock();
            s_busy++;
            bool has = s_marks.TryGetValue(addr, out int f);
            if (has) { s_marks.Remove(addr); s_count--; }
            s_busy--;
            Rt.RuntimeUnlock();
            if (!has) return 1;
            if ((f & Weak) != 0) WeakHandles.Cleared(addr);
            return (f & FinalizerSuppressed) != 0 ? 0 : 1;
        }
    }

    internal interface IWeakOwner { void TargetCleared(int handle, int addr); }

    /// <summary>Handles: a target address (0 once cleared) and an optional owner told when it is cleared.</summary>
    public static class WeakHandles
    {
        static List<int> s_targets, s_owners;
        static Stack<int> s_free;
        static Dictionary<int, List<int>> s_byAddr;

        public static int Alloc(object target, int owner)
        {
            Rt.RuntimeLock();
            s_targets ??= new List<int>(); s_owners ??= new List<int>(); s_free ??= new Stack<int>(); s_byAddr ??= new Dictionary<int, List<int>>();
            int h;
            if (s_free.Count > 0) { h = s_free.Pop(); s_targets[h] = 0; s_owners[h] = owner; }
            else { h = s_targets.Count; s_targets.Add(0); s_owners.Add(owner); }
            Rt.RuntimeUnlock();
            SetTarget(h, target);
            return h;
        }

        public static object Target(int h)
        {
            Rt.RuntimeLock();
            int a = h >= 0 && s_targets != null && h < s_targets.Count ? s_targets[h] : 0;
            object o = a == 0 ? null : Rt.Borrow<object>(a);
            Rt.RuntimeUnlock();
            return o;
        }

        public static void SetTarget(int h, object target)
        {
            Unlink(h);
            if (target == null) return;
            int a = Rt.AddressOf(target);
            Rt.RuntimeLock();
            s_targets[h] = a;
            if (!s_byAddr.TryGetValue(a, out var list)) { list = new List<int>(); s_byAddr[a] = list; }
            list.Add(h);
            Rt.RuntimeUnlock();
            ObjectMarks.Set(target, ObjectMarks.Weak);
        }

        static void Unlink(int h)
        {
            Rt.RuntimeLock();
            int a = s_targets[h];
            if (a != 0)
            {
                s_targets[h] = 0;
                if (s_byAddr.TryGetValue(a, out var list))
                {
                    list.Remove(h);
                    if (list.Count == 0) { s_byAddr.Remove(a); Rt.RuntimeUnlock(); ObjectMarks.ClearAt(a, ObjectMarks.Weak); return; }
                }
            }
            Rt.RuntimeUnlock();
        }

        public static void Free(int h)
        {
            if (s_targets == null || h < 0) return;
            Unlink(h);
            Rt.RuntimeLock();
            s_owners[h] = 0;
            s_free.Push(h);
            Rt.RuntimeUnlock();
        }

        internal static void Cleared(int addr)
        {
            Rt.RuntimeLock();
            if (!s_byAddr.TryGetValue(addr, out var list)) { Rt.RuntimeUnlock(); return; }
            s_byAddr.Remove(addr);
            var handles = list.ToArray();
            for (int i = 0; i < handles.Length; i++) s_targets[handles[i]] = 0;
            Rt.RuntimeUnlock();
            for (int i = 0; i < handles.Length; i++)
            {
                int owner = s_owners[handles[i]];
                if (owner != 0) Rt.Borrow<IWeakOwner>(owner).TargetCleared(handles[i], addr);
            }
        }
    }
}

namespace System
{
    public class WeakReference
    {
        readonly int _h;
        readonly bool _track;
        public WeakReference(object target) : this(target, false) { }
        public WeakReference(object target, bool trackResurrection) { _h = WeakHandles.Alloc(target, 0); _track = trackResurrection; }
        public virtual bool IsAlive => WeakHandles.Target(_h) != null;
        public virtual object Target { get => WeakHandles.Target(_h); set => WeakHandles.SetTarget(_h, value); }
        public virtual bool TrackResurrection => _track;
        ~WeakReference() { WeakHandles.Free(_h); }
    }

    public sealed class WeakReference<T> where T : class
    {
        readonly int _h;
        public WeakReference(T target) : this(target, false) { }
        public WeakReference(T target, bool trackResurrection) { _h = WeakHandles.Alloc(target, 0); }
        public bool TryGetTarget(out T target)
        {
            object o = WeakHandles.Target(_h);
            target = (T)o;
            return o != null;
        }
        public void SetTarget(T target) => WeakHandles.SetTarget(_h, target);
        ~WeakReference() { WeakHandles.Free(_h); }
    }
}

namespace System.Runtime.CompilerServices
{
    /// <summary>Values attached to keys without keeping the keys alive: an entry goes when its key is freed.</summary>
    public sealed class ConditionalWeakTable<TKey, TValue> : IWeakOwner, IEnumerable<KeyValuePair<TKey, TValue>>
        where TKey : class
        where TValue : class
    {
        readonly Dictionary<int, (int handle, TValue value)> _entries = new Dictionary<int, (int, TValue)>();
        public delegate TValue CreateValueCallback(TKey key);

        public ConditionalWeakTable() { }

        public void Add(TKey key, TValue value)
        {
            if (key == null) throw new ArgumentNullException("key");
            if (!TryAdd(key, value)) throw new ArgumentException("An item with the same key has already been added.");
        }

        public bool TryAdd(TKey key, TValue value)
        {
            if (key == null) throw new ArgumentNullException("key");
            int a = Rt.AddressOf(key);
            Rt.RuntimeLock();
            bool exists = _entries.ContainsKey(a);
            Rt.RuntimeUnlock();
            if (exists) return false;
            int h = WeakHandles.Alloc(key, Rt.AddressOf(this));
            Rt.RuntimeLock();
            _entries[a] = (h, value);
            Rt.RuntimeUnlock();
            return true;
        }

        public void AddOrUpdate(TKey key, TValue value)
        {
            if (key == null) throw new ArgumentNullException("key");
            int a = Rt.AddressOf(key);
            Rt.RuntimeLock();
            if (_entries.TryGetValue(a, out var e)) { _entries[a] = (e.handle, value); Rt.RuntimeUnlock(); return; }
            Rt.RuntimeUnlock();
            TryAdd(key, value);
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            if (key == null) throw new ArgumentNullException("key");
            Rt.RuntimeLock();
            bool ok = _entries.TryGetValue(Rt.AddressOf(key), out var e);
            value = ok ? e.value : null;
            Rt.RuntimeUnlock();
            return ok;
        }

        public bool Remove(TKey key)
        {
            if (key == null) throw new ArgumentNullException("key");
            int a = Rt.AddressOf(key);
            Rt.RuntimeLock();
            bool ok = _entries.Remove(a, out var e);
            Rt.RuntimeUnlock();
            if (ok) WeakHandles.Free(e.handle);
            return ok;
        }

        public void Clear()
        {
            Rt.RuntimeLock();
            var hs = new List<int>();
            foreach (var kv in _entries) hs.Add(kv.Value.handle);
            _entries.Clear();
            Rt.RuntimeUnlock();
            foreach (var h in hs) WeakHandles.Free(h);
        }

        public TValue GetValue(TKey key, CreateValueCallback createValueCallback)
        {
            if (TryGetValue(key, out var v)) return v;
            v = createValueCallback(key);
            if (!TryAdd(key, v)) TryGetValue(key, out v);
            return v;
        }

        void IWeakOwner.TargetCleared(int handle, int addr)
        {
            Rt.RuntimeLock();
            bool ok = _entries.TryGetValue(addr, out var e) && e.handle == handle;
            if (ok) _entries.Remove(addr);
            Rt.RuntimeUnlock();
            if (ok) WeakHandles.Free(handle);
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            var list = new List<KeyValuePair<TKey, TValue>>();
            Rt.RuntimeLock();
            foreach (var kv in _entries) list.Add(new KeyValuePair<TKey, TValue>(Rt.Borrow<TKey>(kv.Key), kv.Value.value));
            Rt.RuntimeUnlock();
            return list.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        ~ConditionalWeakTable()
        {
            foreach (var kv in _entries) WeakHandles.Free(kv.Value.handle);
        }
    }
}

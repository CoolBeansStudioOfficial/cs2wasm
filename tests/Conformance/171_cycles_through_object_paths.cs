// Cycles through a field of type object, each closed by a value that reaches the field one way only: the arenas
// take such a field to hold the types its values come from (Sharing.SiteTypeIds), so a way the compiler did not follow
// would leave that cycle out of every group, and its objects would never be freed. One pair of types per way (a
// generic instance per marker), counted by finalizers as in test 127.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

class Tracked
{
    public static readonly List<string> Kinds = new();
    public static readonly int[] Live = new int[64];
    readonly int _kind;
    public Tracked(string kind)
    {
        int k;
        lock (Kinds) { k = Kinds.IndexOf(kind); if (k < 0) { k = Kinds.Count; Kinds.Add(kind); } }
        _kind = k;
        Interlocked.Increment(ref Live[k]);
    }
    ~Tracked() { Interlocked.Decrement(ref Live[_kind]); }
}

// the holder's object field, and the object that holds the holder back
class Holder<K> : Tracked { public object Tag; public object Other; public Holder() : base(typeof(K).Name) { } }
class Back<K> : Tracked { public Holder<K> H; public object S; public Back(Holder<K> h) : base(typeof(K).Name) { H = h; } }
// (a struct inside an object: its reference is a slot of the object's, holding what the struct's field holds)
class InStruct<K> : Tracked { public Box<K> Inner; public InStruct() : base(typeof(K).Name) { } }

struct Box<K> { public object Value; }
struct Pair<K> { public object A; public object B; }
interface IGive { object Give(); }
sealed class Giver<K> : IGive { public object V; public object Give() => V; }
ref struct RefTo { public ref object F; public RefTo(ref object f) { F = ref f; } }

struct ByInterlocked { } struct ByVolatile { } struct ByRefParam { } struct ByRefLocal { } struct BySpan { }
struct BySpanCopy { } struct ByUnsafeAs { } struct ByOut { } struct ByReflection { } struct ByDelegate { }
struct ByLambdaParam { } struct ByGeneric { } struct ByBox { } struct ByRefReturn { } struct ByRefConditional { }
struct ByElement { } struct ByInterface { } struct ByDictionary { } struct ByTaskState { } struct ByRefField { }
struct ByPointer { } struct ByStatic { } struct ByStructField { } struct ByExchange { }
struct ByListSpan { } struct ByArrayCopy { } struct ByTuple { } struct ByRefIn { } struct ByForwardedRef { }
struct ByDynamicInvoke { } struct ByMethodGroup { } struct ByInvoke { } struct ByStructCopy { } struct ByStructStore { } struct ByStructRef { }

static unsafe class P
{
    static object s_slot;

    static void Put(ref object slot, object v) => slot = v;
    static void PutThroughLocal(ref object slot, object v) { ref object s = ref slot; s = v; }
    static void Forward(ref object slot, object v) => Put(ref slot, v);
    static void ForwardKept(ref object slot, object v) => PutThroughLocal(ref slot, v);
    static void Make(out object o, object v) { o = v; }
    static void Store<T>(Holder<ByGeneric> h, T v) => h.Tag = v;
    static ref object SlotOf<K>(Holder<K> h) => ref h.Tag;
    static object Read(in object v) => v;
    static void Exchange(ref object a, ref object b) { var t = a; a = b; b = t; }
    static void SetOther<K>(Holder<K> h, object v) => h.Other = v;
    static void SetBox(ref Box<ByStructRef> b, object v) => b.Value = v;
    public static void SetTag(Holder<ByInvoke> h, object v) => h.Tag = v;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Cycles()
    {
        { var h = new Holder<ByInterlocked>(); Interlocked.CompareExchange(ref h.Tag, new Back<ByInterlocked>(h), null); }
        { var h = new Holder<ByVolatile>(); Volatile.Write(ref h.Tag, new Back<ByVolatile>(h)); }
        { var h = new Holder<ByRefParam>(); Put(ref h.Tag, new Back<ByRefParam>(h)); }
        { var h = new Holder<ByRefLocal>(); PutThroughLocal(ref h.Tag, new Back<ByRefLocal>(h)); }
        { var h = new Holder<ByForwardedRef>(); Forward(ref h.Other, new Back<ByForwardedRef>(h)); ForwardKept(ref h.Tag, new Back<ByForwardedRef>(h)); }
        { var h = new Holder<BySpan>(); MemoryMarshal.CreateSpan(ref h.Tag, 1)[0] = new Back<BySpan>(h); }
        { var h = new Holder<BySpanCopy>(); new object[] { new Back<BySpanCopy>(h) }.AsSpan().CopyTo(MemoryMarshal.CreateSpan(ref h.Tag, 1)); }
        { var h = new Holder<ByUnsafeAs>(); Unsafe.As<object, Back<ByUnsafeAs>>(ref h.Tag) = new Back<ByUnsafeAs>(h); }
        { var h = new Holder<ByOut>(); Make(out h.Tag, new Back<ByOut>(h)); }
        { var h = new Holder<ByReflection>(); typeof(Holder<ByReflection>).GetField("Tag")!.SetValue(h, new Back<ByReflection>(h)); }
        { var h = new Holder<ByDelegate>(); var b = new Back<ByDelegate>(h); Func<object> f = () => b; h.Tag = f(); }
        { var h = new Holder<ByLambdaParam>(); Action<object> set = v => h.Other = v; set(new Back<ByLambdaParam>(h)); }
        { var h = new Holder<ByGeneric>(); Store(h, new Back<ByGeneric>(h)); }
        { var h = new Holder<ByBox>(); h.Tag = new Box<ByBox> { Value = new Back<ByBox>(h) }; }
        { var h = new Holder<ByRefReturn>(); SlotOf(h) = new Back<ByRefReturn>(h); }
        { var h = new Holder<ByRefConditional>(); var g = new Holder<ByRefConditional>(); bool pick = h.GetHashCode() != 0 || true; (pick ? ref h.Tag : ref g.Tag) = new Back<ByRefConditional>(h); }
        { var h = new Holder<ByElement>(); var arr = new object[] { new Back<ByElement>(h) }; h.Tag = arr[0]; }
        { var h = new Holder<ByInterface>(); IGive g = new Giver<ByInterface> { V = new Back<ByInterface>(h) }; h.Tag = g.Give(); }
        { var h = new Holder<ByDictionary>(); var d = new Dictionary<object, object> { ["k"] = new Back<ByDictionary>(h) }; h.Tag = d["k"]; }
        { var h = new Holder<ByTaskState>(); var t = new Task(_ => { }, new Back<ByTaskState>(h)); h.Tag = t.AsyncState; }
        { var h = new Holder<ByRefField>(); var r = new RefTo(ref h.Tag); r.F = new Back<ByRefField>(h); }
        { var h = new Holder<ByPointer>(); Unsafe.AsRef<object>(Unsafe.AsPointer(ref h.Tag)) = new Back<ByPointer>(h); }
        { var h = new Holder<ByStatic>(); s_slot = new Back<ByStatic>(h); h.Tag = s_slot; s_slot = null; }
        { var h = new Holder<ByStructField>(); var p = new Pair<ByStructField> { A = new Back<ByStructField>(h) }; p.B = p.A; h.Tag = p.B; }
        { var h = new Holder<ByExchange>(); object o = new Back<ByExchange>(h); Exchange(ref h.Tag, ref o); }
        { var h = new Holder<ByListSpan>(); var l = new List<object> { null }; CollectionsMarshal.AsSpan(l)[0] = new Back<ByListSpan>(h); h.Tag = l; }
        { var h = new Holder<ByArrayCopy>(); var src = new object[] { new Back<ByArrayCopy>(h) }; var dst = new object[1]; Array.Copy(src, dst, 1); h.Tag = dst; }
        { var h = new Holder<ByTuple>(); var t = (new Back<ByTuple>(h), 1); object o = t; h.Tag = o; }
        { var h = new Holder<ByRefIn>(); object v = new Back<ByRefIn>(h); h.Tag = Read(in v); }
        { var h = new Holder<ByDynamicInvoke>(); Action<object> set = v => h.Tag = v; set.DynamicInvoke(new Back<ByDynamicInvoke>(h)); }
        { var h = new Holder<ByMethodGroup>(); Action<Holder<ByMethodGroup>, object> set = SetOther; set(h, new Back<ByMethodGroup>(h)); }
        { var i = new InStruct<ByStructCopy>(); var b = new Back<ByStructCopy>(null) { S = i }; var bx = new Box<ByStructCopy> { Value = b }; i.Inner = bx; }
        { var i = new InStruct<ByStructStore>(); i.Inner.Value = new Back<ByStructStore>(null) { S = i }; }
        { var i = new InStruct<ByStructRef>(); SetBox(ref i.Inner, new Back<ByStructRef>(null) { S = i }); }
        { var h = new Holder<ByInvoke>(); typeof(P).GetMethod("SetTag")!.Invoke(null, new object[] { h, new Back<ByInvoke>(h) }); }
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Cycles();
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        foreach (var kind in Tracked.Kinds.ToArray()) Console.WriteLine(kind + " " + Tracked.Live[Tracked.Kinds.IndexOf(kind)]);
    }
}

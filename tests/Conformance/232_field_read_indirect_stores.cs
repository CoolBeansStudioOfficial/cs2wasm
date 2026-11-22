// An instance field read in a loop while something the loop reaches only indirectly stores it: a delegate (a lambda
// that is given the object, one that finds it in a static, a method group, a multicast one), an interface and a virtual call with several
// implementations, a function pointer, an event's handlers, a finalizer run where the loop lets go of an object, a
// handler for an exception thrown inside the loop (by the loop and by a callee that stored first), a filter that
// stores the field when a check of the runtime's throws, reflection
// (FieldInfo.SetValue), LINQ's deferred lambdas, a comparer called by a sort, a recursive call, a constructor and a
// static initializer run inside the loop, IDisposable in a `using`. Each field is used by one case only.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Box
{
    public int Lambda, Group, Multi, Iface, Virt, Ptr, Event, Fin, Fin2, Catch, Thrown, Filter, Filter2, Reflect, Linq, Sort, Rec, Ctor, Init, Disp, Quiet;
    public string Name = "n";
    public void SetGroup(int v) { Group = v; }
}

interface ITouch { void Touch(Box b, int v); }
sealed class Plain : ITouch { public void Touch(Box b, int v) { } }
sealed class Storing : ITouch { public void Touch(Box b, int v) { b.Iface = v; } }

abstract class Shape { public abstract void Apply(Box b, int v); public virtual int Read(Box b) => b.Virt; }
sealed class Still : Shape { public override void Apply(Box b, int v) { } }
sealed class Moving : Shape { public override void Apply(Box b, int v) { b.Virt += v; } public override int Read(Box b) { b.Virt++; return b.Virt; } }

sealed class Finalized
{
    readonly Box _b; readonly int _v;
    public Finalized(Box b, int v) { _b = b; _v = v; }
    ~Finalized() { _b.Fin += _v; _b.Fin2 = _b.Fin2 * 2 + 1; }
}

sealed class Maker
{
    public static Box? Target;
    public int Seen;
    public Maker(int v) { if (Target != null) { Seen = Target.Ctor; Target.Ctor = v; } }
}
static class Late
{
    public static readonly int Value;
    // (run at the first read of Value, inside the loop)
    static Late() { Maker.Target!.Init = 500; Value = 1; }
}
sealed class Scope : IDisposable
{
    readonly Box _b;
    public Scope(Box b) { _b = b; }
    public void Dispose() { _b.Disp += 3; }
}
sealed class ByField : IComparer<int>
{
    readonly Box _b;
    public ByField(Box b) { _b = b; }
    public int Compare(int x, int y) { _b.Sort++; return x.CompareTo(y); }
}

static class P
{
    static event Action<Box, int>? Changed;
    static Box? s_box;
    [MethodImpl(MethodImplOptions.NoInlining)] static ITouch Pick(int k) => k == 0 ? new Plain() : new Storing();
    [MethodImpl(MethodImplOptions.NoInlining)] static Shape Shape(int k) => k == 0 ? new Still() : new Moving();
    [MethodImpl(MethodImplOptions.NoInlining)] static void Drop(Box b, int v) { new Finalized(b, v).ToString(); }
    static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    static void StorePtr(Box b, int v) { b.Ptr = v; }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Throws(Box b, int v) { b.Thrown = v; throw new InvalidOperationException("x" + v); }

    static int Delegates(Box b)
    {
        int s = 0;
        // (b stays a variable of this method: no lambda captures it)
        Action<Box, int> a = static (x, v) => x.Lambda = v;
        for (int i = 0; i < 4; i++) { s += b.Lambda; a(b, i + 10); s += b.Lambda; }
        Action<int> g = b.SetGroup;
        for (int i = 0; i < 4; i++) { s += b.Group; g(i + 20); s += b.Group; }
        Action<Box, int> m = static (x, v) => { };
        m += static (x, v) => x.Multi += v;
        m += static (x, v) => x.Multi *= 2;
        for (int i = 0; i < 4; i++) { s += b.Multi; m(b, i + 1); s += b.Multi; }
        Func<Box, int> bump = static x => ++x.Lambda;
        for (int i = 0; i < 4; i++) { s += b.Lambda; s += bump(b); }
        s_box = b;
        Action<int> viaStatic = static v => s_box!.Lambda -= v;
        for (int i = 0; i < 4; i++) { s += b.Lambda; viaStatic(i); s += b.Lambda; }
        return s;
    }
    static int Dispatch(Box b)
    {
        int s = 0;
        for (int k = 0; k < 2; k++)
        {
            var t = Pick(k);
            for (int i = 0; i < 4; i++) { s += b.Iface; t.Touch(b, i + 30 + k); s += b.Iface; }
            var sh = Shape(k);
            for (int i = 0; i < 4; i++) { s += b.Virt; sh.Apply(b, i + 1); s += b.Virt + sh.Read(b); }
        }
        return s;
    }
    static unsafe int Pointers(Box b)
    {
        int s = 0;
        delegate*<Box, int, void> p = &StorePtr;
        for (int i = 0; i < 4; i++) { s += b.Ptr; p(b, i + 40); s += b.Ptr; }
        return s;
    }
    static int Events(Box b)
    {
        int s = 0;
        Action<Box, int> h = static (x, v) => x.Event += v;
        Changed += h;
        for (int i = 0; i < 4; i++) { s += b.Event; Changed?.Invoke(b, i + 1); s += b.Event; if (i == 2) Changed -= h; }
        return s;
    }
    static int Finalizers(Box b)
    {
        int s = 0;
        for (int i = 0; i < 4; i++) { Collect(); s += b.Fin; Drop(b, i + 50); Collect(); s += b.Fin + b.Fin2; }
        // (the object let go of in the loop's own code: the store over the element that held it)
        var slot = new object?[1];
        for (int i = 0; i < 4; i++) { Collect(); s += b.Fin; slot[0] = Make(b, i + 60); Collect(); s += b.Fin + b.Fin2; }
        return s;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static object Make(Box b, int v) => new Finalized(b, v);
    static int Handlers(Box b)
    {
        int s = 0;
        for (int i = 0; i < 4; i++)
        {
            try { s += b.Catch; if (i == 1) throw new InvalidOperationException("here"); s += b.Catch; }
            catch (InvalidOperationException e) { s += e.Message.Length + b.Catch; b.Catch = 70; }
            finally { b.Catch += 2; }
            s += b.Catch;
        }
        return s + Filters(b);
    }
    static int Filters(Box b)
    {
        int s = 0;
        for (int i = 0; i < 4; i++)
        {
            try { s += b.Thrown; if ((i & 1) == 1) Throws(b, 80 + i); s += b.Thrown; }
            catch (InvalidOperationException e) when (e.Message.Length > b.Thrown - 100) { s += b.Thrown; }
            s += b.Thrown;
        }
        return s;
    }
    // a filter that stores the field (through a static: b stays a variable of the method), run by a throw of the
    // runtime's own (a null array's length, an index out of range) and by one in a callee, before the handler
    static bool Touch(int v) { s_box!.Filter += v; s_box.Filter2 = s_box.Filter2 * 3 + v; return true; }
    [MethodImpl(MethodImplOptions.NoInlining)] static int At(int[] a, int i) => a[i];
    static int FilterStores(Box b, int[]? none)
    {
        int s = 0;
        s_box = b;
        var one = new int[1];
        for (int i = 0; i < 4; i++)
        {
            try { s += b.Filter; if (i > 0) s += none!.Length; }
            catch (NullReferenceException) when (Touch(i)) { s += 1; }
            s += b.Filter;
        }
        for (int i = 0; i < 4; i++)
        {
            try { s += b.Filter; s += At(one, i); s += one[i & 2]; }
            catch (IndexOutOfRangeException) when (Touch(i + 10)) { s += 2; }
            s += b.Filter;
        }
        // (in a straight line: a read before the try statement and one after it)
        int before = b.Filter2;
        try { s += none!.Length; } catch (NullReferenceException) when (Touch(5)) { }
        s += before + b.Filter2;
        before = b.Filter2;
        try { s += At(one, 3); } catch (IndexOutOfRangeException) when (Touch(6)) { }
        return s + before + b.Filter2;
    }
    static int Reflected(Box b)
    {
        int s = 0;
        var f = typeof(Box).GetField("Reflect")!;
        for (int i = 0; i < 4; i++) { s += b.Reflect; f.SetValue(b, i + 90); s += b.Reflect + (int)f.GetValue(b)!; }
        var n = typeof(Box).GetField("Name")!;
        for (int i = 0; i < 4; i++) { s += b.Name.Length; n.SetValue(b, new string('r', i + 2)); s += b.Name.Length; }
        return s;
    }
    static int Deferred(Box b)
    {
        int s = 0;
        var items = new List<int> { 3, 1, 2, 5, 4 };
        s_box = b;
        foreach (var v in items.Where(static x => { s_box!.Linq += x; return x > 1; }).Select(static x => x + s_box!.Linq)) { s += v + b.Linq; }
        for (int i = 0; i < 3; i++) { s += b.Sort; items.Sort(new ByField(b)); s += b.Sort; items.Reverse(); }
        for (int i = 0; i < 3; i++) { s += b.Sort; items.Sort(static (x, y) => { s_box!.Sort += 2; return y - x; }); s += b.Sort; }
        return s;
    }
    static int Recursive(Box b, int depth)
    {
        int s = 0;
        for (int i = 0; i < 2; i++)
        {
            s += b.Rec;
            if (depth > 0) s += Recursive(b, depth - 1); else b.Rec++;
            s += b.Rec;
        }
        return s;
    }
    static int Constructed(Box b)
    {
        int s = 0;
        Maker.Target = b;
        for (int i = 0; i < 4; i++) { s += b.Ctor; var m = new Maker(i + 100); s += b.Ctor + m.Seen; }
        for (int i = 0; i < 4; i++) { s += b.Init; if (i == 2) s += Late.Value; s += b.Init; }
        for (int i = 0; i < 4; i++) { s += b.Disp; using (new Scope(b)) { s += b.Disp; } s += b.Disp; }
        return s;
    }
    // (nothing the loop reaches stores Quiet)
    static int Quiet(Box b)
    {
        int s = 0;
        Action<Box, int> a = static (x, v) => x.Lambda = v;
        var t = Pick(1);
        for (int i = 0; i < 4; i++) { s += b.Quiet; a(b, i); t.Touch(b, i); Drop(b, 1); s += b.Quiet; }
        return s;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        var b = new Box { Quiet = 9 };
        Console.WriteLine("delegates " + Delegates(b));
        Console.WriteLine("dispatch " + Dispatch(b));
        Console.WriteLine("pointers " + Pointers(b));
        Console.WriteLine("events " + Events(b));
        Console.WriteLine("finalizers " + Finalizers(b));
        Console.WriteLine("handlers " + Handlers(b));
        Console.WriteLine("filters " + FilterStores(b, null));
        Console.WriteLine("reflected " + Reflected(b));
        Console.WriteLine("deferred " + Deferred(b));
        Console.WriteLine("recursive " + Recursive(b, 3));
        Console.WriteLine("constructed " + Constructed(b));
        Console.WriteLine("quiet " + Quiet(b));
    }
}

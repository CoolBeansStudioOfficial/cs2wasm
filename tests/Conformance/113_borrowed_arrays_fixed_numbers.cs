// A local that reads an array field and uses only its length and elements (section 4.1: it borrows, though another
// branch stores the field), `fixed` over a variable that keeps its value, and number methods called on the number.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Bag<T>
{
    T[] _items = new T[2];
    int _size;
    public int Count => _size;

    public void Add(T item)
    {
        T[] array = _items;
        int size = _size;
        if ((uint)size < (uint)array.Length) { _size = size + 1; array[size] = item; }
        else AddWithResize(item);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void AddWithResize(T item)
    {
        var bigger = new T[_items.Length * 2];
        Array.Copy(_items, bigger, _size);
        _items = bigger;
        _items[_size++] = item;
    }

    // the array is used after the branch that replaces it: the old one must still be there
    public int AddThenLook(T item)
    {
        T[] array = _items;
        if (_size == array.Length) AddWithResize(item);
        else { array[_size] = item; _size++; }
        return array.Length;
    }

    // in a loop: the next round uses the local after the round before replaced the field
    public int Fill(T item, int n)
    {
        T[] array = _items;
        int old = 0;
        for (int i = 0; i < n; i++)
        {
            if (_size < array.Length) { array[_size] = item; _size++; old++; }
            else AddWithResize(item);
        }
        return old * 1000 + array.Length;
    }

    public T this[int i] => _items[i];
}

sealed class Ints
{
    int[] _a = new int[4];
    int _n;
    public long SumAfterGrow()
    {
        int[] a = _a;
        long s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i];
        Grow();
        // (no use of a after the store: nothing to keep)
        return s + _a.Length;
    }
    public long LookAfterGrow()
    {
        int[] a = _a;
        a[0] = 7;
        Grow();
        return a[0] * 100 + a.Length + _a.Length;   // the old array, after the field has another
    }
    void Grow() { var b = new int[_a.Length * 2]; Array.Copy(_a, b, _a.Length); _a = b; _n++; }
}

sealed class Holder { public string? Text; public char[]? Chars; }

static unsafe class Program
{
    static readonly Holder s_holder = new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static string Make(char c, int n) => new string(c, n);

    static int FixedLocal()
    {
        string s = Make('a', 12);
        int sum = 0;
        fixed (char* p = s)
        {
            for (int i = 0; i < 12; i++) sum += p[i];
        }
        return sum + s.Length;
    }

    static int FixedMoved()
    {
        // the variable's value is handed to a field inside the statement, and the field is cleared: the pointer's
        // own hold on the string is what keeps it
        string s = Make('b', 12);
        int sum = 0;
        fixed (char* p = s)
        {
            s_holder.Text = s;
            s_holder.Text = null;
            string other = Make('z', 12);
            sum += p[0] + p[11] + other.Length;
        }
        return sum;
    }

    static int FixedReassigned()
    {
        string s = Make('c', 8);
        int sum = 0;
        fixed (char* p = s)
        {
            s = Make('d', 8);
            string other = Make('y', 8);
            sum += p[0] + p[7] + s[0] + other.Length;
        }
        return sum;
    }

    static int FixedParam(string s, char[] cs)
    {
        int sum = 0;
        fixed (char* p = s) fixed (char* q = cs)
        {
            sum = p[0] + q[cs.Length - 1];
            q[0] = 'Q';
        }
        return sum + cs[0];
    }

    static int FixedArrayField()
    {
        s_holder.Chars = new char[6];
        char[] a = s_holder.Chars;
        int r;
        fixed (char* p = a)
        {
            s_holder.Chars = null;
            char[] other = new char[6];
            other[0] = 'x';
            p[0] = 'k';
            r = p[0] + other[0];
        }
        return r + a[0];
    }

    static string Numbers()
    {
        int i = -1234567; uint u = 4000000000; long l = long.MinValue + 1; ulong ul = ulong.MaxValue; short sh = -12; byte b = 200;
        double d = 1234.5678; float f = 0.1f; char c = 'x'; bool t = true; nint n = -5;
        var inv = CultureInfo.InvariantCulture;
        var parts = new List<string>
        {
            i.ToString(), u.ToString(), l.ToString(), ul.ToString(), sh.ToString(), b.ToString(), d.ToString(inv), f.ToString(inv), c.ToString(), t.ToString(), n.ToString(),
            i.ToString("X8"), d.ToString("F2", inv), i.CompareTo(5).ToString(), i.Equals(-1234567).ToString(), u.GetHashCode().ToString(), l.CompareTo(0L).ToString(),
            c.CompareTo('a').ToString(), t.CompareTo(false).ToString(), d.Equals(1234.5678).ToString(), b.CompareTo((byte)201).ToString(), 0.ToString(), 7.ToString(), 10.ToString(),
            int.MaxValue.ToString(), int.MinValue.ToString(), (i + 1).ToString(), Math.Abs(i).ToString(),
        };
        Span<char> buf = stackalloc char[16];
        i.TryFormat(buf, out int w);
        parts.Add(new string(buf[..w]));
        parts.Add(Generic(41, 42) + "," + Generic(2.5, 2.5) + "," + Generic('b', 'a'));
        return string.Join(" ", parts);
    }

    static string Generic<T>(T a, T b) where T : IComparable<T>, IEquatable<T> => a.CompareTo(b) + ":" + a.Equals(b) + ":" + a.ToString() + ":" + a.GetHashCode().Equals(b.GetHashCode());

    static string Boxed()
    {
        object o = 42; IFormattable fm = 3.5; IComparable cmp = 7;
        Func<string> viaDelegate = 12345.ToString;
        return o.ToString() + " " + fm.ToString(null, CultureInfo.InvariantCulture) + " " + cmp.CompareTo(8) + " " + viaDelegate() + " " + o.Equals(42) + " " + o.GetHashCode();
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var bag = new Bag<string>();
        for (int i = 0; i < 40; i++) bag.Add("s" + i);
        Console.WriteLine(bag.Count + " " + bag[0] + " " + bag[39]);
        var bi = new Bag<int>();
        for (int i = 0; i < 1000; i++) bi.Add(i * 3);
        Console.WriteLine(bi.Count + " " + bi[999]);
        var b2 = new Bag<string>();
        Console.WriteLine(b2.AddThenLook("a") + " " + b2.AddThenLook("b") + " " + b2.AddThenLook("c") + " " + b2.AddThenLook("d") + " " + b2.AddThenLook("e") + " " + b2[4]);
        var b3 = new Bag<object>();
        Console.WriteLine(b3.Fill("x", 7) + " " + b3.Count + " " + b3[6]);
        var ints = new Ints();
        Console.WriteLine(ints.SumAfterGrow() + " " + ints.LookAfterGrow());
        Console.WriteLine(FixedLocal() + " " + FixedMoved() + " " + FixedReassigned() + " " + FixedParam("pq", new[] { 'a', 'b', 'c' }) + " " + FixedArrayField());
        Console.WriteLine(Numbers());
        Console.WriteLine(Boxed());
        long total = 0;
        for (int i = 0; i < 20000; i++) total += i.ToString().Length + ((long)i * i).ToString().Length;
        Console.WriteLine(total);
    }
}

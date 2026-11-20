// A pooled builder of tuples of a reference and two bools used as a stack, as Roslyn's GreenNode.WriteTo does:
// ArrayBuilder<(GreenNode node, bool leading, bool trailing)> taken from a static pool, pushed and popped over a tree
// of nodes, freed back to the pool. The tuples' references must stay whole and counted through every move.
using System;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

abstract class Green
{
    public abstract int SlotCount { get; }
    public abstract Green? GetSlot(int i);
    public virtual bool IsToken => false;
    public virtual void WriteToken(StringBuilder sb, bool leading, bool trailing) { }

    public override string ToString()
    {
        var sb = new StringBuilder();
        WriteTo(sb, true, true);
        return sb.ToString();
    }

    public void WriteTo(StringBuilder sb, bool leading, bool trailing)
    {
        var stack = Builder<(Green node, bool leading, bool trailing)>.GetInstance();
        stack.Push((this, leading, trailing));
        Process(sb, stack);
        stack.Free();

        static void Process(StringBuilder sb, Builder<(Green node, bool leading, bool trailing)> stack)
        {
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                var node = current.node;
                if (node.IsToken) { node.WriteToken(sb, current.leading, current.trailing); continue; }
                int first = 0, last = node.SlotCount - 1;
                for (int i = last; i >= first; i--)
                {
                    var child = node.GetSlot(i);
                    if (child != null)
                        stack.Push((child, current.leading | i != first, current.trailing | i != last));
                }
            }
        }
    }
}

sealed class Token : Green
{
    readonly string _text;
    public Token(string text) { _text = text; }
    public override int SlotCount => 0;
    public override Green? GetSlot(int i) => null;
    public override bool IsToken => true;
    public override void WriteToken(StringBuilder sb, bool leading, bool trailing)
    {
        if (leading) sb.Append('<');
        sb.Append(_text);
        if (trailing) sb.Append('>');
    }
}

sealed class Node : Green
{
    readonly Green?[] _slots;
    public Node(params Green?[] slots) { _slots = slots; }
    public override int SlotCount => _slots.Length;
    public override Green? GetSlot(int i) => _slots[i];
}

sealed class Builder<T>
{
    static readonly Pool s_pool = new Pool();
    readonly ImmutableArray<T>.Builder _builder = ImmutableArray.CreateBuilder<T>();
    public int Count => _builder.Count;
    public void Push(T item) => _builder.Add(item);
    public T Pop()
    {
        int p = _builder.Count - 1;
        var r = _builder[p];
        _builder.RemoveAt(p);
        return r;
    }
    public static Builder<T> GetInstance() => s_pool.Allocate();
    public void Free()
    {
        if (_builder.Count <= 128) { _builder.Clear(); s_pool.Free(this); }
    }

    sealed class Pool
    {
        readonly Builder<T>?[] _items = new Builder<T>?[4];
        Builder<T>? _first;
        public Builder<T> Allocate()
        {
            var f = _first;
            if (f != null && f == Interlocked.CompareExchange(ref _first, null, f)) return f;
            for (int i = 0; i < _items.Length; i++)
            {
                var x = _items[i];
                if (x != null && x == Interlocked.CompareExchange(ref _items[i], null, x)) return x;
            }
            return new Builder<T>();
        }
        public void Free(Builder<T> b)
        {
            if (_first == null) { _first = b; return; }
            for (int i = 0; i < _items.Length; i++)
                if (_items[i] == null) { _items[i] = b; return; }
        }
    }
}

static class Program
{
    static Green Make(int depth, ref int n)
    {
        if (depth == 0) return new Token((n++).ToString());
        var slots = new Green?[3];
        for (int i = 0; i < 3; i++) slots[i] = (i + depth) % 4 == 3 ? null : Make(depth - 1, ref n);
        return new Node(slots);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        int n = 0;
        var tree = Make(5, ref n);
        int len = 0;
        string s = "";
        for (int k = 0; k < 50; k++) { s = tree.ToString(); len += s.Length; }
        // and on threads at once, sharing the tree and the pool (as Roslyn's parse on several threads)
        var tasks = new System.Threading.Tasks.Task<int>[8];
        for (int t = 0; t < tasks.Length; t++)
            tasks[t] = System.Threading.Tasks.Task.Run(() => { int l = 0; for (int k = 0; k < 50; k++) l += tree.ToString().Length; return l; });
        foreach (var t in tasks) len += t.Result;
        Console.WriteLine(s.Substring(0, 40));
        Console.WriteLine(len + " " + n);
    }
}

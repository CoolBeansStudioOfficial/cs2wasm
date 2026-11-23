// A reference argument the method never uses (and a this the method never reads): its count on entry and its
// release must come in that order, or the caller's object is freed while it is still held.
using System;

class Holder
{
    public string Name = "kept";
    public virtual int Size(int n) => n * 3; // (this unused)
}

static class Program
{
    static int Ignore(object o, int x) => x + 1;

    static void Main()
    {
        var h = new Holder();
        int s = 0;
        for (int i = 0; i < 5; i++) { s += h.Size(i); s += Ignore(h, i); s += Ignore(h.Name, i); }
        Console.WriteLine(s);
        Console.WriteLine(h.Name);
        var again = new Holder();
        Console.WriteLine(again.Name + h.Name);
    }
}

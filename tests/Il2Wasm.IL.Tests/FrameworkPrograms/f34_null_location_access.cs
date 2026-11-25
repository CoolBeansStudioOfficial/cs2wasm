// Interlocked and Volatile operations on a null location throw NullReferenceException before they read or write
// anything (a plain load of address 0 would not trap in the engine). The operations that throw are counted, and the
// first one that does not throw is named.
using System;
using System.Runtime.CompilerServices;
using System.Threading;

static class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Count(int which)
    {
        try
        {
            switch (which)
            {
                case 0: Interlocked.Read(ref Unsafe.NullRef<long>()); break;
                case 1: Interlocked.Increment(ref Unsafe.NullRef<int>()); break;
                case 2: Interlocked.Add(ref Unsafe.NullRef<long>(), 1); break;
                case 3: Interlocked.Exchange(ref Unsafe.NullRef<byte>(), 1); break;
                case 4: Interlocked.CompareExchange(ref Unsafe.NullRef<ushort>(), 1, 1); break;
                case 5: Interlocked.Exchange<string?>(ref Unsafe.NullRef<string?>(), null); break;
                case 6: Interlocked.CompareExchange<object?>(ref Unsafe.NullRef<object?>(), null, null); break;
                case 7: Interlocked.And(ref Unsafe.NullRef<uint>(), 1); break;
                case 8: Interlocked.Or(ref Unsafe.NullRef<ulong>(), 1); break;
                case 9: Volatile.Read(ref Unsafe.NullRef<bool>()); break;
                case 10: Volatile.Write(ref Unsafe.NullRef<int>(), 1); break;
                case 11: Volatile.Read(ref Unsafe.NullRef<EqualReference>()); break;
                case 12: Volatile.Write(ref Unsafe.NullRef<double>(), 1.5); break;
                case 13: Volatile.Read(ref Unsafe.NullRef<sbyte>()); break;
                default: return -1;
            }
            return 0;
        }
        catch (NullReferenceException) { return 1; }
    }

    sealed class EqualReference { }

    static void Main()
    {
        int thrown = 0;
        int firstQuiet = -1;
        for (int k = 0; k < 14; k++)
        {
            int r = Count(k);
            if (r == 1) thrown++;
            else if (firstQuiet < 0) firstQuiet = k;
        }
        Console.WriteLine("thrown " + thrown + ", first quiet " + firstQuiet);

        // (the value of a location that exists is still read and written)
        int live = 5;
        Console.WriteLine(Interlocked.Increment(ref live) + " " + Volatile.Read(ref live));
        Volatile.Write(ref live, 9);
        Console.WriteLine(Interlocked.Exchange(ref live, 3) + " " + live);
    }
}

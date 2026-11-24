// unbox and unbox.any of enums and primitives, as the CLR's casting rules allow (IlCompiler.UnboxFn, UnboxableKind):
// an enum's box unboxes as its underlying type, the underlying type's box as the enum, and one enum's box as another
// enum of the same underlying type. A different size, or the other sign of the same size (int and uint), throws
// InvalidCastException; so do bool and byte, char and ushort. Through a Nullable<T> too.
using System;

enum E : int { A = 1, B = 2 }
enum F : int { X = 2 }
enum U : uint { Big = 4000000000 }
enum S : short { M = -5 }
enum Bt : byte { One = 1 }

static class Program
{
    static T Un<T>(object o) => (T)o;
    static string Try<T>(object o)
    {
        try { return Un<T>(o)!.ToString()!; }
        catch (InvalidCastException) { return "ICE"; }
        catch (NullReferenceException) { return "NRE"; }
    }

    static void Main()
    {
        object e = E.B, i = 7, u = 9u, s = (short)-3, b = (byte)200, bo = true, ch = 'c', us = (ushort)99, l = 5L, big = U.Big;
        Console.WriteLine("enum as int: " + Try<int>(e) + ", int as enum: " + Try<E>(i) + ", enum as enum: " + Try<F>(e));
        Console.WriteLine("enum as uint: " + Try<uint>(e) + ", uint as enum: " + Try<E>(u) + ", U as uint: " + Try<uint>(big) + ", U as int: " + Try<int>(big));
        Console.WriteLine("enum as long: " + Try<long>(e) + ", long as enum: " + Try<E>(l) + ", short as S: " + Try<S>(s) + ", S as short: " + Try<short>((object)S.M));
        Console.WriteLine("byte as Bt: " + Try<Bt>(b) + ", Bt as byte: " + Try<byte>((object)Bt.One) + ", Bt as sbyte: " + Try<sbyte>((object)Bt.One) + ", bool as byte: " + Try<byte>(bo) + ", byte as bool: " + Try<bool>(b));
        Console.WriteLine("char as ushort: " + Try<ushort>(ch) + ", ushort as char: " + Try<char>(us) + ", int as uint: " + Try<uint>(i) + ", uint as int: " + Try<int>(u));
        Console.WriteLine("nullable: " + Try<int?>(e) + " " + Try<E?>(i) + " " + Try<long?>(e) + " " + Try<E?>(null!) + "; null as int: " + Try<int>(null!));
        // unbox (the address form): a field of the box read in place
        Console.WriteLine("unbox: " + ((E)i == E.A) + " " + ((int)e + 1) + " " + (Try<E>(i) == "7"));
    }
}

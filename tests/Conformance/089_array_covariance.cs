// An array of a derived element type used as an array of the base type: a store of anything else is an
// ArrayTypeMismatchException, a copy an InvalidCastException, as in .NET.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
class A { } class B : A { } sealed class C : B { }
interface IX { } class X1 : IX { } class X2 : IX { }
static class Program
{
    static void T(string name, Action a)
    {
        try { a(); Console.WriteLine(name + " ok"); } catch (Exception e) { Console.WriteLine(name + " threw " + e.GetType().Name + ": " + e.Message); }
    }
    static void Put(object[] a, object v) => a[0] = v;
    static void PutA(A[] a, A v) => a[0] = v;
    static void PutX(IX[] a, IX v) => a[0] = v;
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        T("string[] <- int", () => Put(new string[1], 1));
        T("string[] <- string", () => Put(new string[1], "s"));
        T("string[] <- null", () => Put(new string[1], null!));
        T("object[] <- int", () => Put(new object[1], 1));
        T("B[] <- A", () => PutA(new B[1], new A()));
        T("B[] <- B", () => PutA(new B[1], new B()));
        T("B[] <- C", () => PutA(new B[1], new C()));
        T("C[] <- B", () => PutA(new C[1], new B()));
        T("A[] <- C", () => PutA(new A[1], new C()));
        T("X1[] <- X2", () => PutX(new X1[1], new X2()));
        T("X1[] <- X1", () => PutX(new X1[1], new X1()));
        T("IX[] <- X2", () => PutX(new IX[1], new X2()));
        T("A[][] <- B[]", () => Put(new A[1][], new B[1]));
        T("B[][] <- A[]", () => Put(new B[1][], new A[1]));
        T("object[] as IX[] <- string", () => Put(new IX[1], "s"));
        T("int[][] <- int[]", () => Put(new int[1][], new int[2]));
        T("int[][] <- uint[]", () => Put(new int[1][], new long[2]));
        T("compound", () => { object[] a = new string[] { "a" }; a[0] += "b"; Console.Write(a[0] + " "); });
        T("list", () => { var l = new List<object>(); l.Add("s"); l.Add(1); l[0] = 2.5; IList<A> la = new B[2]; la[0] = new B(); try { la[1] = new A(); } catch (ArrayTypeMismatchException) { Console.Write("mismatch "); } });
        T("Array.Copy", () => { object[] src = { "a", 1 }; try { Array.Copy(src, new string[2], 2); } catch (InvalidCastException) { Console.Write("cast "); } Array.Copy(new object[] { "a", "b" }, new string[2], 2); try { new object[] { 1 }.CopyTo(new string[1], 0); } catch (InvalidCastException) { Console.Write("cast2 "); } });
        T("SetValue", () => { Array a = new string[1]; try { a.SetValue(1, 0); } catch (InvalidCastException) { Console.Write("cast "); } a.SetValue("x", 0); });
    }
}

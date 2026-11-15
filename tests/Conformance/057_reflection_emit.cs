using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

public class Point { public int X; public int Y; public static int Made; public Point(int x, int y) { X = x; Y = y; Made++; } public int Sum() => X + Y; public virtual string Describe() => "Point(" + X + "," + Y + ")"; }
public class Point3 : Point { public int Z; public Point3(int x, int y, int z) : base(x, y) { Z = z; } public override string Describe() => "Point3(" + X + "," + Y + "," + Z + ")"; }
public struct Pair { public int A; public long B; public void Bump() { A++; B += 10; } public override string ToString() => A + ":" + B; }
public enum Color { Red, Green = 5, Blue }

static class Program {
  static DynamicMethod Make(string name, Type ret, Type[] ps, Action<ILGenerator> body) {
    var dm = new DynamicMethod(name, ret, ps, typeof(Program).Module);
    body(dm.GetILGenerator());
    return dm;
  }

  static int Triple(int x) => 3 * x;

  [UnmanagedCallersOnly(EntryPoint = "main")]
  static void Main() {
    // arithmetic and arguments
    var add = (Func<int, int, int>)Make("Add", typeof(int), [typeof(int), typeof(int)], il => {
      il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Add);
      il.Emit(OpCodes.Ldc_I4_3); il.Emit(OpCodes.Mul); il.Emit(OpCodes.Ret);
    }).CreateDelegate(typeof(Func<int, int, int>));
    Console.WriteLine(add(2, 5));

    // a loop with locals and branches: sum of 1..n
    var sum = (Func<int, long>)Make("Sum", typeof(long), [typeof(int)], il => {
      var acc = il.DeclareLocal(typeof(long)); var i = il.DeclareLocal(typeof(int));
      var top = il.DefineLabel(); var test = il.DefineLabel();
      il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Stloc, acc);
      il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stloc, i);
      il.Emit(OpCodes.Br, test);
      il.MarkLabel(top);
      il.Emit(OpCodes.Ldloc, acc); il.Emit(OpCodes.Ldloc, i); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, acc);
      il.Emit(OpCodes.Ldloc, i); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, i);
      il.MarkLabel(test);
      il.Emit(OpCodes.Ldloc, i); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ble, top);
      il.Emit(OpCodes.Ldloc, acc); il.Emit(OpCodes.Ret);
    }).CreateDelegate(typeof(Func<int, long>));
    Console.WriteLine(sum(100000));

    // calls, strings, objects, fields, virtual calls
    var describe = (Func<int, string>)Make("Describe", typeof(string), [typeof(int)], il => {
      il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_2); il.Emit(OpCodes.Ldc_I4, 7);
      il.Emit(OpCodes.Newobj, typeof(Point3).GetConstructor([typeof(int), typeof(int), typeof(int)])!);
      il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldc_I4, 40); il.Emit(OpCodes.Stfld, typeof(Point).GetField("Y")!);
      il.Emit(OpCodes.Callvirt, typeof(Point).GetMethod("Describe")!);
      il.Emit(OpCodes.Ldstr, " made ");
      il.Emit(OpCodes.Ldsflda, typeof(Point).GetField("Made")!);
      il.Emit(OpCodes.Call, typeof(int).GetMethod("ToString", Type.EmptyTypes)!);
      il.Emit(OpCodes.Call, typeof(string).GetMethod("Concat", [typeof(string), typeof(string), typeof(string)])!);
      il.Emit(OpCodes.Ret);
    }).CreateDelegate(typeof(Func<int, string>));
    Console.WriteLine(describe(1));

    // structs: a local's address, initobj, a method that changes it, fields, boxing
    var pairs = (Func<int, string>)Make("Pairs", typeof(string), [typeof(int)], il => {
      var p = il.DeclareLocal(typeof(Pair));
      il.Emit(OpCodes.Ldloca, p); il.Emit(OpCodes.Initobj, typeof(Pair));
      il.Emit(OpCodes.Ldloca, p); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Stfld, typeof(Pair).GetField("A")!);
      il.Emit(OpCodes.Ldloca, p); il.Emit(OpCodes.Call, typeof(Pair).GetMethod("Bump")!);
      il.Emit(OpCodes.Ldloca, p); il.Emit(OpCodes.Call, typeof(Pair).GetMethod("Bump")!);
      il.Emit(OpCodes.Ldloc, p); il.Emit(OpCodes.Box, typeof(Pair));
      il.Emit(OpCodes.Callvirt, typeof(object).GetMethod("ToString")!);
      il.Emit(OpCodes.Ret);
    }).CreateDelegate(typeof(Func<int, string>));
    Console.WriteLine(pairs(5));

    // arrays, conversions, boxing and unboxing, isinst, enums
    var arrays = Make("Arrays", typeof(object), [typeof(int)], il => {
      var a = il.DeclareLocal(typeof(double[]));
      il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Newarr, typeof(double)); il.Emit(OpCodes.Stloc, a);
      il.Emit(OpCodes.Ldloc, a); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ldc_R8, 2.5); il.Emit(OpCodes.Stelem_R8);
      il.Emit(OpCodes.Ldloc, a); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ldelema, typeof(double)); il.Emit(OpCodes.Dup);
      il.Emit(OpCodes.Ldind_R8); il.Emit(OpCodes.Ldc_R8, 4.0); il.Emit(OpCodes.Mul); il.Emit(OpCodes.Stind_R8);
      il.Emit(OpCodes.Ldloc, a); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ldelem_R8);
      il.Emit(OpCodes.Ldloc, a); il.Emit(OpCodes.Ldlen); il.Emit(OpCodes.Conv_R8); il.Emit(OpCodes.Add);
      il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add);
      il.Emit(OpCodes.Box, typeof(Color));
      il.Emit(OpCodes.Ret);
    });
    object r = arrays.Invoke(null, [5])!;
    Console.WriteLine(r + " " + r.GetType().Name + " " + (r is Color));

    var unbox = (Func<object, int>)Make("Unbox", typeof(int), [typeof(object)], il => {
      var notInt = il.DefineLabel();
      il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Isinst, typeof(int)); il.Emit(OpCodes.Brfalse_S, notInt);
      il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Unbox_Any, typeof(int)); il.Emit(OpCodes.Ret);
      il.MarkLabel(notInt);
      il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, typeof(string));
      il.Emit(OpCodes.Callvirt, typeof(string).GetProperty("Length")!.GetGetMethod()!); il.Emit(OpCodes.Neg); il.Emit(OpCodes.Ret);
    }).CreateDelegate(typeof(Func<object, int>));
    Console.WriteLine(unbox(42) + " " + unbox("hello"));
    try { unbox(new List<int>()); } catch (InvalidCastException) { Console.WriteLine("InvalidCastException"); }

    // switch, comparisons, unsigned and float arithmetic
    var classify = (Func<int, string>)Make("Classify", typeof(string), [typeof(int)], il => {
      var l0 = il.DefineLabel(); var l1 = il.DefineLabel(); var l2 = il.DefineLabel(); var other = il.DefineLabel();
      il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Switch, [l0, l1, l2]);
      il.Emit(OpCodes.Br, other);
      il.MarkLabel(l0); il.Emit(OpCodes.Ldstr, "zero"); il.Emit(OpCodes.Ret);
      il.MarkLabel(l1); il.Emit(OpCodes.Ldstr, "one"); il.Emit(OpCodes.Ret);
      il.MarkLabel(l2); il.Emit(OpCodes.Ldstr, "two"); il.Emit(OpCodes.Ret);
      il.MarkLabel(other);
      il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Clt);
      il.Emit(OpCodes.Box, typeof(bool)); il.Emit(OpCodes.Callvirt, typeof(object).GetMethod("ToString")!); il.Emit(OpCodes.Ret);
    }).CreateDelegate(typeof(Func<int, string>));
    Console.WriteLine(classify(0) + " " + classify(2) + " " + classify(9) + " " + classify(-4));

    var mixed = (Func<uint, double, string>)Make("Mixed", typeof(string), [typeof(uint), typeof(double)], il => {
      il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_3); il.Emit(OpCodes.Div_Un);
      il.Emit(OpCodes.Ldc_I4_7); il.Emit(OpCodes.Shr_Un); il.Emit(OpCodes.Conv_U8); il.Emit(OpCodes.Conv_R_Un);
      il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldc_R8, 3.0); il.Emit(OpCodes.Rem); il.Emit(OpCodes.Add);
      il.Emit(OpCodes.Conv_R4); il.Emit(OpCodes.Box, typeof(float));
      il.Emit(OpCodes.Callvirt, typeof(object).GetMethod("ToString")!); il.Emit(OpCodes.Ret);
    }).CreateDelegate(typeof(Func<uint, double, string>));
    Console.WriteLine(mixed(4000000000u, 10.5));

    // exceptions: throw, catch by type, finally, rethrow, checked arithmetic
    var log = new List<string>();
    var guarded = (Func<List<string>, int, int>)Make("Guarded", typeof(int), [typeof(List<string>), typeof(int)], il => {
      var result = il.DeclareLocal(typeof(int));
      var addLog = typeof(List<string>).GetMethod("Add")!;
      il.BeginExceptionBlock();
        il.BeginExceptionBlock();
          il.Emit(OpCodes.Ldc_I4, int.MaxValue); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Add_Ovf); il.Emit(OpCodes.Stloc, result);
          il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Div); il.Emit(OpCodes.Stloc, result);
        il.BeginCatchBlock(typeof(OverflowException));
          il.Emit(OpCodes.Callvirt, typeof(Exception).GetProperty("Message")!.GetGetMethod()!); il.Emit(OpCodes.Pop);
          il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldstr, "overflow"); il.Emit(OpCodes.Callvirt, addLog);
          il.Emit(OpCodes.Ldc_I4_M1); il.Emit(OpCodes.Stloc, result);
        il.BeginFinallyBlock();
          il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldstr, "inner finally"); il.Emit(OpCodes.Callvirt, addLog);
        il.EndExceptionBlock();
      il.BeginCatchBlock(typeof(DivideByZeroException));
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldstr, "divide"); il.Emit(OpCodes.Callvirt, addLog);
        il.Emit(OpCodes.Ldc_I4, -2); il.Emit(OpCodes.Stloc, result);
      il.EndExceptionBlock();
      il.Emit(OpCodes.Ldloc, result); il.Emit(OpCodes.Ret);
    }).CreateDelegate(typeof(Func<List<string>, int, int>));
    Console.WriteLine(guarded(log, 1) + " " + guarded(log, 0) + " " + string.Join(",", log));

    var thrower = (Action<string>)Make("Thrower", typeof(void), [typeof(string)], il => {
      il.Emit(OpCodes.Ldarg_0);
      il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor([typeof(string)])!);
      il.Emit(OpCodes.Throw);
    }).CreateDelegate(typeof(Action<string>));
    try { thrower("bad state"); } catch (InvalidOperationException e) { Console.WriteLine("caught " + e.Message); }
    try { Make("Thrower2", typeof(void), [typeof(string)], il => {
      il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Newobj, typeof(ArgumentException).GetConstructor([typeof(string)])!); il.Emit(OpCodes.Throw);
    }).Invoke(null, ["wrapped"]); }
    catch (TargetInvocationException e) { Console.WriteLine(e.InnerException!.GetType().Name + " " + e.InnerException.Message); }

    // a closed delegate (the first argument bound), ldtoken, a method that calls another dynamic method
    var square = Make("Square", typeof(int), [typeof(int)], il => { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Dup); il.Emit(OpCodes.Mul); il.Emit(OpCodes.Ret); });
    var useSquare = (Func<int, int>)Make("UseSquare", typeof(int), [typeof(int)], il => {
      il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, square); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Ret);
    }).CreateDelegate(typeof(Func<int, int>));
    Console.WriteLine(useSquare(12));
    var bound = (Func<int, int>)Make("Bound", typeof(int), [typeof(Point), typeof(int)], il => {
      il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, typeof(Point).GetMethod("Sum")!); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Ret);
    }).CreateDelegate(typeof(Func<int, int>), new Point(30, 12));
    Console.WriteLine(bound(2));
    var typeName = (Func<string>)Make("TypeName", typeof(string), Type.EmptyTypes, il => {
      il.Emit(OpCodes.Ldtoken, typeof(Dictionary<string, int>));
      il.Emit(OpCodes.Call, typeof(Type).GetMethod("GetTypeFromHandle")!);
      il.Emit(OpCodes.Callvirt, typeof(MemberInfo).GetProperty("Name")!.GetGetMethod()!); il.Emit(OpCodes.Ret);
    }).CreateDelegate(typeof(Func<string>));
    Console.WriteLine(typeName());

    // a filter, rethrow, a delegate over ldftn, a ref parameter
    var filtered = (Func<int, string>)Make("Filtered", typeof(string), [typeof(int)], il => {
      var r = il.DeclareLocal(typeof(string));
      il.BeginExceptionBlock();
        il.BeginExceptionBlock();
          il.Emit(OpCodes.Ldstr, "code"); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Box, typeof(int));
          il.Emit(OpCodes.Call, typeof(string).GetMethod("Concat", [typeof(object), typeof(object)])!);
          il.Emit(OpCodes.Newobj, typeof(Exception).GetConstructor([typeof(string)])!); il.Emit(OpCodes.Throw);
        il.BeginExceptFilterBlock();
          il.Emit(OpCodes.Castclass, typeof(Exception)); il.Emit(OpCodes.Callvirt, typeof(Exception).GetProperty("Message")!.GetGetMethod()!);
          il.Emit(OpCodes.Ldstr, "code1"); il.Emit(OpCodes.Call, typeof(string).GetMethod("op_Equality")!);
        il.BeginCatchBlock(null);
          il.Emit(OpCodes.Pop); il.Emit(OpCodes.Ldstr, "filtered"); il.Emit(OpCodes.Stloc, r);
        il.BeginCatchBlock(typeof(Exception));
          il.Emit(OpCodes.Pop); il.Emit(OpCodes.Rethrow);
        il.EndExceptionBlock();
      il.BeginCatchBlock(typeof(Exception));
        il.Emit(OpCodes.Callvirt, typeof(Exception).GetProperty("Message")!.GetGetMethod()!); il.Emit(OpCodes.Ldstr, " rethrown"); 
        il.Emit(OpCodes.Call, typeof(string).GetMethod("Concat", [typeof(string), typeof(string)])!); il.Emit(OpCodes.Stloc, r);
      il.EndExceptionBlock();
      il.Emit(OpCodes.Ldloc, r); il.Emit(OpCodes.Ret);
    }).CreateDelegate(typeof(Func<int, string>));
    Console.WriteLine(filtered(1) + " " + filtered(2));
    var makeDelegate = (Func<Func<int, int>>)Make("MakeDelegate", typeof(Func<int, int>), Type.EmptyTypes, il => {
      il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Ldftn, typeof(Program).GetMethod("Triple", BindingFlags.Static | BindingFlags.NonPublic)!);
      il.Emit(OpCodes.Newobj, typeof(Func<int, int>).GetConstructors()[0]); il.Emit(OpCodes.Ret);
    }).CreateDelegate(typeof(Func<Func<int, int>>));
    Console.WriteLine(makeDelegate()(14));
    var swap = Make("Swap", typeof(void), [typeof(int).MakeByRefType(), typeof(int).MakeByRefType()], il => {
      var t = il.DeclareLocal(typeof(int));
      il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldind_I4); il.Emit(OpCodes.Stloc, t);
      il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldind_I4); il.Emit(OpCodes.Stind_I4);
      il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldloc, t); il.Emit(OpCodes.Stind_I4); il.Emit(OpCodes.Ret);
    });
    object?[] sargs = [3, 9];
    swap.Invoke(null, sargs);
    Console.WriteLine(sargs[0] + " " + sargs[1]);

    var dm = Make("Info", typeof(int), [typeof(string), typeof(int)], il => { il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ret); });
    Console.WriteLine(dm.Name + " " + dm.ReturnType + " " + dm.GetParameters().Length + " " + dm.IsStatic + " " + dm.Attributes);
    try { new DynamicMethod("Empty", typeof(void), Type.EmptyTypes).CreateDelegate(typeof(Action)); }
    catch (InvalidOperationException e) { Console.WriteLine(e.Message); }
  }
}

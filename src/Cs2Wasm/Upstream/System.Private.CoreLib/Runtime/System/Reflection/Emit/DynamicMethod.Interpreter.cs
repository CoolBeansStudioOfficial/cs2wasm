// cs2wasm: there is no code generation in the sandbox, so a DynamicMethod's IL runs on this interpreter (section 8:
// "Runtime code generation runs on a bundled interpreter"). It is linked in only when the program creates a
// DynamicMethod. Calls, field accesses and object creation go through reflection (the members the IL names are the
// ones the program's reflection found, so the compiler kept their metadata and thunks).
//
// Values on the evaluation stack: int (int32, native int, bool, char and the small integers: the target is 32-bit),
// long (int64), double (F), object references, a box for a value of another value type (always a copy of its own,
// marked so `dup` copies it again), Ref (a managed pointer) and MethodPointer (ldftn).

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace System.Reflection.Emit
{
    public sealed partial class DynamicMethod
    {
        private IlInterpreter? _interpreter;

        internal IlInterpreter Interpreter => _interpreter!;
    }

    internal sealed class IlInterpreter
    {
        // a managed pointer
        internal abstract class Ref
        {
            public abstract object? Get();
            public abstract void Set(object? v);
        }

        sealed class SlotRef(object?[] slots, int index) : Ref
        {
            public override object? Get() => slots[index];
            public override void Set(object? v) => slots[index] = v;
        }

        sealed class ArrayRef(Array array, int index) : Ref
        {
            public override object? Get() => array.GetValue(index);
            public override void Set(object? v) => array.SetValue(v, index);
        }

        // a field of an object, of a value a pointer points at (read, changed and written back), or a static field
        sealed class FieldRef(object? owner, Ref? ownerRef, FieldInfo field) : Ref
        {
            public override object? Get() => field.GetValue(ownerRef != null ? ownerRef.Get() : owner);
            public override void Set(object? v)
            {
                if (ownerRef == null) { field.SetValue(owner, v); return; }
                object? o = ownerRef.Get();
                field.SetValue(o, v);
                ownerRef.Set(o);
            }
        }

        // the value inside a box (unbox): changes are made in the box itself
        sealed class BoxRef(object box) : Ref
        {
            public override object? Get() => box;
            public override void Set(object? v)
            {
                if (v == null || v.GetType() != box.GetType()) throw new InvalidCastException();
                CopyInto(box, v);
            }
        }

        internal sealed class MethodPointer(MethodBase method)
        {
            public readonly MethodBase Method = method;
        }

        struct Clause
        {
            public int Kind; // ILExceptionBlock.CATCH, FILTER, FINALLY, FAULT
            public int TryStart, TryEnd, HandlerStart, HandlerEnd, FilterStart;
            public Type? CatchType;
            public bool Covers(int pc) => TryStart <= pc && pc < TryEnd;
            public bool InHandler(int pc) => HandlerStart <= pc && pc < HandlerEnd;
        }

        // what runs when an endfinally is reached: the next finally, then the leave's target, the catch, or leaving the method
        struct Next
        {
            public int Kind; // 0 finally (Clause), 1 leave (Target), 2 catch (Clause, Exception), 3 escape (Exception)
            public int Clause, Target;
            public Exception? Exception;
        }

        sealed class Batch
        {
            public readonly List<Next> Steps = new();
            public int Position;
            public int Running = -1; // the finally clause being run
        }

        sealed class Frame
        {
            public object?[] Args = null!;
            public object?[] Locals = null!;
            public object?[] Stack = new object?[16];
            public bool[] Fresh = new bool[16]; // a value-type value (its box is the value's own copy)
            public int Sp;
            public int Pc;
            public List<Batch>? Batches;
            public Exception?[]? Caught; // per clause: the exception its catch handler is handling
            public object? Result;
            public Exception? Escape;

            public void Push(object? v, bool fresh = false)
            {
                if (Sp == Stack.Length)
                {
                    Array.Resize(ref Stack, Sp * 2);
                    Array.Resize(ref Fresh, Sp * 2);
                }
                Stack[Sp] = v;
                Fresh[Sp++] = fresh;
            }

            public object? Pop() => Stack[--Sp];
        }

        readonly DynamicMethod _method;
        readonly byte[] _code;
        readonly int _length;
        readonly Type[] _localTypes;
        readonly Type[] _paramTypes;
        readonly Type _returnType;
        readonly object?[] _refs;
        readonly Clause[] _clauses; // innermost first

        internal IlInterpreter(DynamicMethod method, RuntimeILGenerator il, object?[]? refs, Type[] paramTypes, Type returnType)
        {
            _method = method;
            _code = il.Code;
            _length = il.ILOffset;
            var locals = il.LocalBuilders;
            _localTypes = new Type[locals?.Length ?? 0];
            for (int i = 0; i < _localTypes.Length; i++) _localTypes[i] = locals![i].LocalType;
            _paramTypes = paramTypes;
            _returnType = returnType;
            _refs = refs ?? [];
            var clauses = new List<Clause>();
            foreach (var info in il.ExceptionInfos ?? [])
            {
                if (info.handlers == null) continue;
                foreach (var h in info.handlers)
                {
                    var c = new Clause { Kind = h.type, HandlerStart = h.start, HandlerEnd = h.start + h.len, TryStart = info.start, CatchType = h.extype };
                    // a finally or fault after catch clauses protects them too (the nesting C# and the CLR's ILGenerator give)
                    c.TryEnd = h.type is ILExceptionBlock.FINALLY or ILExceptionBlock.FAULT ? h.start : info.start + info.len;
                    if (h.type == ILExceptionBlock.FILTER) c.FilterStart = h.filter_offset;
                    clauses.Add(c);
                }
            }
            // innermost first (a stable sort: the clauses of one try keep their order)
            var order = new int[clauses.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) =>
            {
                int la = clauses[a].TryEnd - clauses[a].TryStart, lb = clauses[b].TryEnd - clauses[b].TryStart;
                return la != lb ? la.CompareTo(lb) : a.CompareTo(b);
            });
            _clauses = new Clause[order.Length];
            for (int i = 0; i < order.Length; i++) _clauses[i] = clauses[order[i]];
        }

        // ---- entry ----

        internal object? Run(object?[] args)
        {
            var f = new Frame { Args = args, Locals = new object?[_localTypes.Length] };
            for (int i = 0; i < _localTypes.Length; i++) f.Locals[i] = Default(_localTypes[i]);
            int pc = 0;
            while (true)
            {
                try
                {
                    if (Execute(f, pc, filter: false)) return f.Result;
                    ExceptionDispatchInfo.Capture(f.Escape!).Throw();
                }
                catch (Exception ex)
                {
                    if (f.Escape == ex) throw;
                    pc = Dispatch(f, ex);
                    if (pc < 0) throw;
                }
            }
        }

        // ---- exceptions ----

        /// <summary>Where execution goes for an exception thrown at f.Pc: the first finally to run or the catch handler;
        /// -1 when nothing in this method handles it or runs for it.</summary>
        int Dispatch(Frame f, Exception ex)
        {
            int at = f.Pc;
            int target = -1;
            for (int i = 0; i < _clauses.Length && target < 0; i++)
            {
                ref Clause c = ref _clauses[i];
                if (!c.Covers(at)) continue;
                if (c.Kind == ILExceptionBlock.CATCH && (c.CatchType == null || c.CatchType.IsInstanceOfType(ex))) target = i;
                else if (c.Kind == ILExceptionBlock.FILTER && RunFilter(f, c.FilterStart, ex)) target = i;
            }
            // the finally blocks being run that the exception leaves are abandoned
            while (f.Batches is { Count: > 0 } bs)
            {
                var top = bs[bs.Count - 1];
                if (top.Running < 0) { bs.RemoveAt(bs.Count - 1); continue; }
                ref Clause running = ref _clauses[top.Running];
                if (!running.InHandler(at)) { bs.RemoveAt(bs.Count - 1); continue; }
                if (target >= 0 && running.HandlerStart <= _clauses[target].TryStart && _clauses[target].TryEnd <= running.HandlerEnd) break;
                if (target < 0 || !running.InHandler(_clauses[target].HandlerStart)) { bs.RemoveAt(bs.Count - 1); continue; }
                break;
            }
            var batch = new Batch();
            for (int i = 0; i < _clauses.Length; i++)
            {
                if (i == target) break;
                ref Clause c = ref _clauses[i];
                if (c.Kind is not (ILExceptionBlock.FINALLY or ILExceptionBlock.FAULT) || !c.Covers(at)) continue;
                if (target >= 0 && c.Covers(_clauses[target].HandlerStart)) continue;
                batch.Steps.Add(new Next { Kind = 0, Clause = i });
            }
            if (batch.Steps.Count == 0)
            {
                if (target < 0) return -1;
                return EnterCatch(f, target, ex);
            }
            batch.Steps.Add(target >= 0 ? new Next { Kind = 2, Clause = target, Exception = ex } : new Next { Kind = 3, Exception = ex });
            (f.Batches ??= new()).Add(batch);
            return Continue(f, batch);
        }

        int EnterCatch(Frame f, int clause, Exception ex)
        {
            f.Sp = 0;
            f.Push(ex);
            (f.Caught ??= new Exception?[_clauses.Length])[clause] = ex;
            return _clauses[clause].HandlerStart;
        }

        /// <summary>The next step of a batch; -2 when it leaves the method with an exception (f.Escape).</summary>
        int Continue(Frame f, Batch b)
        {
            var step = b.Steps[b.Position++];
            if (b.Position == b.Steps.Count) f.Batches!.Remove(b);
            f.Sp = 0;
            switch (step.Kind)
            {
                case 0: b.Running = step.Clause; return _clauses[step.Clause].HandlerStart;
                case 1: return step.Target;
                case 2: return EnterCatch(f, step.Clause, step.Exception!);
                default: f.Escape = step.Exception; return -2;
            }
        }

        bool RunFilter(Frame f, int start, Exception ex)
        {
            int sp = f.Sp; int pc = f.Pc;
            var saved = new object?[sp];
            Array.Copy(f.Stack, saved, sp);
            try
            {
                f.Sp = 0;
                f.Push(ex);
                Execute(f, start, filter: true);
                return f.Result is int r && r != 0;
            }
            catch (Exception)
            {
                return false; // an exception in a filter makes it false
            }
            finally
            {
                Array.Copy(saved, f.Stack, sp);
                f.Sp = sp; f.Pc = pc;
            }
        }

        int Leave(Frame f, int from, int target)
        {
            var batch = new Batch();
            for (int i = 0; i < _clauses.Length; i++)
            {
                ref Clause c = ref _clauses[i];
                if (c.Kind == ILExceptionBlock.FINALLY && c.Covers(from) && !c.Covers(target)) batch.Steps.Add(new Next { Kind = 0, Clause = i });
            }
            // leaving a catch handler ends its handling
            if (f.Caught != null)
                for (int i = 0; i < _clauses.Length; i++)
                    if (f.Caught[i] != null && _clauses[i].InHandler(from) && !_clauses[i].InHandler(target)) f.Caught[i] = null;
            f.Sp = 0;
            if (batch.Steps.Count == 0) return target;
            batch.Steps.Add(new Next { Kind = 1, Target = target });
            (f.Batches ??= new()).Add(batch);
            return Continue(f, batch);
        }

        Exception CurrentException(Frame f, int pc)
        {
            Exception? best = null; int size = int.MaxValue;
            for (int i = 0; i < _clauses.Length; i++)
            {
                ref Clause c = ref _clauses[i];
                if (f.Caught?[i] is { } e && c.InHandler(pc) && c.HandlerEnd - c.HandlerStart < size) { best = e; size = c.HandlerEnd - c.HandlerStart; }
            }
            return best ?? throw new InvalidProgramException();
        }

        // ---- the loop ----

        int I4(int pc) => _code[pc] | _code[pc + 1] << 8 | _code[pc + 2] << 16 | _code[pc + 3] << 24;
        object? Token(int pc) => _refs[I4(pc) - 1];

        /// <summary>Runs from pc until ret (true; f.Result) or an exception leaves the method after its finally blocks
        /// (false; f.Escape); in a filter, until endfilter (f.Result: the filter's int).</summary>
        bool Execute(Frame f, int pc, bool filter)
        {
            Type? constrained = null;
            while (true)
            {
                if (pc < 0) return false;
                if (pc >= _length) throw new InvalidProgramException();
                f.Pc = pc;
                int op = _code[pc++];
                switch (op)
                {
                    case 0x00: break; // nop
                    case 0x01: break; // break
                    case 0x02: case 0x03: case 0x04: case 0x05: LdArg(f, op - 0x02); break;
                    case 0x06: case 0x07: case 0x08: case 0x09: LdLoc(f, op - 0x06); break;
                    case 0x0A: case 0x0B: case 0x0C: case 0x0D: StLoc(f, op - 0x0A); break;
                    case 0x0E: LdArg(f, _code[pc++]); break;
                    case 0x0F: f.Push(new SlotRef(f.Args, _code[pc++])); break;
                    case 0x10: StArg(f, _code[pc++]); break;
                    case 0x11: LdLoc(f, _code[pc++]); break;
                    case 0x12: f.Push(new SlotRef(f.Locals, _code[pc++])); break;
                    case 0x13: StLoc(f, _code[pc++]); break;
                    case 0x14: f.Push(null); break;
                    case 0x15: f.Push(-1); break;
                    case >= 0x16 and <= 0x1E: f.Push(op - 0x16); break;
                    case 0x1F: f.Push((int)(sbyte)_code[pc++]); break;
                    case 0x20: f.Push(I4(pc)); pc += 4; break;
                    case 0x21: f.Push((long)(uint)I4(pc) | (long)I4(pc + 4) << 32); pc += 8; break;
                    case 0x22: f.Push((double)BitConverter.Int32BitsToSingle(I4(pc))); pc += 4; break;
                    case 0x23: f.Push(BitConverter.Int64BitsToDouble((long)(uint)I4(pc) | (long)I4(pc + 4) << 32)); pc += 8; break;
                    case 0x25: // dup
                    {
                        object? v = f.Stack[f.Sp - 1];
                        bool fresh = f.Fresh[f.Sp - 1];
                        f.Push(fresh ? Clone(v!) : v, fresh);
                        break;
                    }
                    case 0x26: f.Sp--; break; // pop
                    case 0x28: Call(f, (MethodBase)Token(pc)!, virt: false, null); pc += 4; break;
                    case 0x29: // calli
                    {
                        pc += 4;
                        var mp = (MethodPointer)f.Pop()!;
                        Call(f, mp.Method, virt: false, null);
                        break;
                    }
                    case 0x2A: // ret
                        f.Result = _returnType == typeof(void) ? null : FromStack(f.Pop(), _returnType);
                        return true;
                    case >= 0x2B and <= 0x37: // short branches
                    {
                        int d = (sbyte)_code[pc++];
                        if (Branch(f, op - 0x2B)) pc += d;
                        break;
                    }
                    case >= 0x38 and <= 0x44: // branches
                    {
                        int d = I4(pc); pc += 4;
                        if (Branch(f, op - 0x38)) pc += d;
                        break;
                    }
                    case 0x45: // switch
                    {
                        int n = I4(pc); pc += 4;
                        int end = pc + 4 * n;
                        int v = AsI4(f.Pop());
                        pc = (uint)v < (uint)n ? end + I4(pc + 4 * v) : end;
                        break;
                    }
                    case 0x46: f.Push((int)(sbyte)AsI4(Deref(f))); break;
                    case 0x47: f.Push((int)(byte)AsI4(Deref(f))); break;
                    case 0x48: f.Push((int)(short)AsI4(Deref(f))); break;
                    case 0x49: f.Push((int)(ushort)AsI4(Deref(f))); break;
                    case 0x4A: case 0x4B: case 0x4D: f.Push(AsI4(Deref(f))); break;
                    case 0x4C: f.Push(AsI8(Deref(f))); break;
                    case 0x4E: case 0x4F: f.Push(AsR8(Deref(f))); break;
                    case 0x50: f.Push(((Ref)f.Pop()!).Get()); break;
                    case 0x51: { object? v = f.Pop(); ((Ref)f.Pop()!).Set(v); break; } // stind.ref
                    case 0x52: StInd(f, typeof(sbyte)); break;
                    case 0x53: StInd(f, typeof(short)); break;
                    case 0x54: StInd(f, typeof(int)); break;
                    case 0x55: StInd(f, typeof(long)); break;
                    case 0x56: StInd(f, typeof(float)); break;
                    case 0x57: StInd(f, typeof(double)); break;
                    case >= 0x58 and <= 0x64: { object? b = f.Pop(); object? a = f.Pop(); f.Push(Binary(op, a, b)); break; }
                    case 0x65: f.Push(f.Pop() switch { int i => -i, long l => -l, double d => -d, _ => throw new InvalidProgramException() }); break;
                    case 0x66: f.Push(f.Pop() switch { int i => ~i, long l => ~l, _ => throw new InvalidProgramException() }); break;
                    case 0x67: f.Push(Conv(f.Pop(), TypeCode.SByte)); break;
                    case 0x68: f.Push(Conv(f.Pop(), TypeCode.Int16)); break;
                    case 0x69: f.Push(Conv(f.Pop(), TypeCode.Int32)); break;
                    case 0x6A: f.Push(Conv(f.Pop(), TypeCode.Int64)); break;
                    case 0x6B: f.Push(Conv(f.Pop(), TypeCode.Single)); break;
                    case 0x6C: f.Push(Conv(f.Pop(), TypeCode.Double)); break;
                    case 0x6D: f.Push(Conv(f.Pop(), TypeCode.UInt32)); break;
                    case 0x6E: f.Push(Conv(f.Pop(), TypeCode.UInt64)); break;
                    case 0x6F: Call(f, (MethodBase)Token(pc)!, virt: true, constrained); constrained = null; pc += 4; break;
                    case 0x70: { var t = (Type)Token(pc)!; pc += 4; var src = (Ref)f.Pop()!; var dst = (Ref)f.Pop()!; dst.Set(Copy(src.Get(), t)); break; } // cpobj
                    case 0x71: { var t = (Type)Token(pc)!; pc += 4; PushTyped(f, ((Ref)f.Pop()!).Get(), t, copy: true); break; } // ldobj
                    case 0x72: f.Push((string)Token(pc)!); pc += 4; break;
                    case 0x73: NewObj(f, (ConstructorInfo)Token(pc)!); pc += 4; break;
                    case 0x74: // castclass
                    {
                        var t = (Type)Token(pc)!; pc += 4;
                        object? o = f.Stack[f.Sp - 1];
                        if (o != null && !IsInstance(o, t)) throw CastError(o, t);
                        break;
                    }
                    case 0x75: // isinst
                    {
                        var t = (Type)Token(pc)!; pc += 4;
                        object? o = f.Stack[f.Sp - 1];
                        if (o != null && !IsInstance(o, t)) f.Stack[f.Sp - 1] = null;
                        f.Fresh[f.Sp - 1] = false;
                        break;
                    }
                    case 0x76: f.Push(f.Pop() switch { int i => (double)(uint)i, long l => (double)(ulong)l, double d => d, _ => throw new InvalidProgramException() }); break; // conv.r.un
                    case 0x79: // unbox
                    {
                        var t = (Type)Token(pc)!; pc += 4;
                        object o = f.Pop() ?? throw new NullReferenceException();
                        if (!IsInstance(o, t)) throw CastError(o, t);
                        f.Push(new BoxRef(o));
                        break;
                    }
                    case 0x7A: // throw
                    {
                        object o = f.Pop() ?? throw new NullReferenceException();
                        throw o as Exception ?? new RuntimeWrappedException(o);
                    }
                    case 0x7B: // ldfld
                    {
                        var fi = (FieldInfo)Token(pc)!; pc += 4;
                        object? o = f.Pop();
                        object? owner = o is Ref r ? r.Get() : o;
                        if (owner == null && !fi.IsStatic) throw new NullReferenceException();
                        PushTyped(f, fi.GetValue(owner), fi.FieldType, copy: false);
                        break;
                    }
                    case 0x7C: // ldflda
                    {
                        var fi = (FieldInfo)Token(pc)!; pc += 4;
                        object? o = f.Pop();
                        if (o == null) throw new NullReferenceException();
                        f.Push(o is Ref r ? new FieldRef(null, r, fi) : new FieldRef(o, null, fi));
                        break;
                    }
                    case 0x7D: // stfld
                    {
                        var fi = (FieldInfo)Token(pc)!; pc += 4;
                        object? v = FromStack(f.Pop(), fi.FieldType);
                        object? o = f.Pop();
                        if (o == null) throw new NullReferenceException();
                        if (o is Ref r) new FieldRef(null, r, fi).Set(v);
                        else fi.SetValue(o, v);
                        break;
                    }
                    case 0x7E: { var fi = (FieldInfo)Token(pc)!; pc += 4; PushTyped(f, fi.GetValue(null), fi.FieldType, copy: false); break; }
                    case 0x7F: { var fi = (FieldInfo)Token(pc)!; pc += 4; f.Push(new FieldRef(null, null, fi)); break; }
                    case 0x80: { var fi = (FieldInfo)Token(pc)!; pc += 4; fi.SetValue(null, FromStack(f.Pop(), fi.FieldType)); break; }
                    case 0x81: { var t = (Type)Token(pc)!; pc += 4; object? v = FromStack(f.Pop(), t); ((Ref)f.Pop()!).Set(v); break; } // stobj
                    case >= 0x82 and <= 0x8B: f.Push(ConvOvfUn(f.Pop(), op - 0x82)); break;
                    case 0x8C: // box
                    {
                        var t = (Type)Token(pc)!; pc += 4;
                        object? v = f.Pop();
                        f.Push(t.IsValueType ? Box(v, t) : v);
                        break;
                    }
                    case 0x8D: // newarr
                    {
                        var t = (Type)Token(pc)!; pc += 4;
                        int n = AsI4(f.Pop());
                        if (n < 0) throw new OverflowException(SR.Arg_OverflowException);
                        f.Push(Array.CreateInstance(t, n));
                        break;
                    }
                    case 0x8E: f.Push(((Array?)f.Pop() ?? throw new NullReferenceException()).Length); break; // ldlen
                    case 0x8F: // ldelema
                    {
                        pc += 4;
                        int i = AsI4(f.Pop());
                        var a = (Array?)f.Pop() ?? throw new NullReferenceException();
                        if ((uint)i >= (uint)a.Length) throw new IndexOutOfRangeException();
                        f.Push(new ArrayRef(a, i));
                        break;
                    }
                    case >= 0x90 and <= 0x9A: LdElem(f, null); break;
                    case >= 0x9B and <= 0xA2: StElem(f, null); break;
                    case 0xA3: { var t = (Type)Token(pc)!; pc += 4; LdElem(f, t); break; }
                    case 0xA4: { var t = (Type)Token(pc)!; pc += 4; StElem(f, t); break; }
                    case 0xA5: // unbox.any
                    {
                        var t = (Type)Token(pc)!; pc += 4;
                        object? o = f.Pop();
                        if (!t.IsValueType)
                        {
                            if (o != null && !IsInstance(o, t)) throw CastError(o, t);
                            f.Push(o);
                            break;
                        }
                        if (Nullable.GetUnderlyingType(t) is { } u)
                        {
                            if (o != null && !IsInstance(o, u)) throw CastError(o, t);
                            f.Push(o, o != null);
                            break;
                        }
                        if (o == null) throw new NullReferenceException();
                        if (!IsInstance(o, t)) throw CastError(o, t);
                        PushTyped(f, o, t, copy: true);
                        break;
                    }
                    case >= 0xB3 and <= 0xBA: f.Push(ConvOvf(f.Pop(), op - 0xB3)); break;
                    case 0xC3: // ckfinite
                    {
                        double d = AsR8(f.Stack[f.Sp - 1]);
                        if (!double.IsFinite(d)) throw new ArithmeticException(SR.Arg_ArithmeticException);
                        break;
                    }
                    case 0xD0: // ldtoken
                    {
                        object? t = Token(pc); pc += 4;
                        object h = t switch
                        {
                            Type ty => ty.TypeHandle,
                            MethodBase m => m.MethodHandle,
                            FieldInfo fi => fi.FieldHandle,
                            _ => throw new InvalidProgramException(),
                        };
                        f.Push(h, true);
                        break;
                    }
                    case 0xD1: f.Push(Conv(f.Pop(), TypeCode.UInt16)); break;
                    case 0xD2: f.Push(Conv(f.Pop(), TypeCode.Byte)); break;
                    case 0xD3: f.Push(Conv(f.Pop(), TypeCode.Int32)); break; // conv.i
                    case 0xD4: f.Push(ConvOvf(f.Pop(), 4)); break; // conv.ovf.i
                    case 0xD5: f.Push(ConvOvf(f.Pop(), 5)); break; // conv.ovf.u
                    case >= 0xD6 and <= 0xDB: { object? b = f.Pop(); object? a = f.Pop(); f.Push(Overflowing(op, a, b)); break; }
                    case 0xDC: // endfinally
                    {
                        if (f.Batches is not { Count: > 0 } bs) throw new InvalidProgramException();
                        pc = Continue(f, bs[bs.Count - 1]);
                        if (pc == -2) return false;
                        break;
                    }
                    case 0xDD: { int d = I4(pc); pc += 4; pc = Leave(f, f.Pc, pc + d); if (pc == -2) return false; break; }
                    case 0xDE: { int d = (sbyte)_code[pc++]; pc = Leave(f, f.Pc, pc + d); if (pc == -2) return false; break; }
                    case 0xDF: StInd(f, typeof(int)); break; // stind.i
                    case 0xE0: f.Push(Conv(f.Pop(), TypeCode.UInt32)); break; // conv.u
                    case 0xFE:
                    {
                        int op2 = _code[pc++];
                        switch (op2)
                        {
                            case 0x01: { object? b = f.Pop(); object? a = f.Pop(); f.Push(Compare(0, a, b) ? 1 : 0); break; } // ceq
                            case 0x02: { object? b = f.Pop(); object? a = f.Pop(); f.Push(Compare(3, a, b) ? 1 : 0); break; } // cgt
                            case 0x03: { object? b = f.Pop(); object? a = f.Pop(); f.Push(Compare(8, a, b) ? 1 : 0); break; } // cgt.un
                            case 0x04: { object? b = f.Pop(); object? a = f.Pop(); f.Push(Compare(5, a, b) ? 1 : 0); break; } // clt
                            case 0x05: { object? b = f.Pop(); object? a = f.Pop(); f.Push(Compare(10, a, b) ? 1 : 0); break; } // clt.un
                            case 0x06: f.Push(new MethodPointer((MethodBase)Token(pc)!)); pc += 4; break; // ldftn
                            case 0x07: f.Pop(); f.Push(new MethodPointer((MethodBase)Token(pc)!)); pc += 4; break; // ldvirtftn
                            case 0x09: LdArg(f, _code[pc] | _code[pc + 1] << 8); pc += 2; break;
                            case 0x0A: f.Push(new SlotRef(f.Args, _code[pc] | _code[pc + 1] << 8)); pc += 2; break;
                            case 0x0B: StArg(f, _code[pc] | _code[pc + 1] << 8); pc += 2; break;
                            case 0x0C: LdLoc(f, _code[pc] | _code[pc + 1] << 8); pc += 2; break;
                            case 0x0D: f.Push(new SlotRef(f.Locals, _code[pc] | _code[pc + 1] << 8)); pc += 2; break;
                            case 0x0E: StLoc(f, _code[pc] | _code[pc + 1] << 8); pc += 2; break;
                            case 0x11: // endfilter
                                if (!filter) throw new InvalidProgramException();
                                f.Result = AsI4(f.Pop());
                                return true;
                            case 0x12: pc++; break; // unaligned.
                            case 0x13: break; // volatile.
                            case 0x14: break; // tail.
                            case 0x15: { var t = (Type)Token(pc)!; pc += 4; ((Ref)f.Pop()!).Set(Default(t)); break; } // initobj
                            case 0x16: constrained = (Type)Token(pc)!; pc += 4; break;
                            case 0x19: pc++; break; // no.
                            case 0x1A: throw CurrentException(f, f.Pc); // rethrow (the caught exception, its trace kept)
                            case 0x1C: { var t = (Type)Token(pc)!; pc += 4; f.Push(SizeOf(t)); break; }
                            case 0x1E: break; // readonly.
                            default: throw new NotSupportedException("cs2wasm: the IL instruction 0xFE " + op2.ToString("X2") + " is not supported by the interpreter");
                        }
                        break;
                    }
                    default: throw new NotSupportedException("cs2wasm: the IL instruction 0x" + op.ToString("X2") + " is not supported by the interpreter");
                }
            }
        }

        // ---- arguments and locals ----

        void LdArg(Frame f, int i)
        {
            var t = _paramTypes[i];
            if (t.IsByRef) { f.Push(f.Args[i] as Ref ?? new SlotRef(f.Args, i)); return; }
            PushTyped(f, f.Args[i], t, copy: true);
        }

        void StArg(Frame f, int i) => f.Args[i] = FromStack(f.Pop(), _paramTypes[i]);
        void LdLoc(Frame f, int i) => PushTyped(f, f.Locals[i], _localTypes[i], copy: true);
        void StLoc(Frame f, int i) => f.Locals[i] = FromStack(f.Pop(), _localTypes[i]);

        static object? Deref(Frame f)
        {
            object? v = ((Ref)f.Pop()!).Get();
            return v == null ? null : ToStack(v, v.GetType());
        }

        static void StInd(Frame f, Type t)
        {
            object? v = f.Pop();
            var r = (Ref)f.Pop()!;
            // the location's own type decides the stored representation (stind.i4 into an uint, a char...)
            object? cur = r.Get();
            r.Set(FromStack(v, cur?.GetType() ?? t));
        }

        // ---- arrays ----

        static void LdElem(Frame f, Type? t)
        {
            int i = AsI4(f.Pop());
            var a = (Array?)f.Pop() ?? throw new NullReferenceException();
            if ((uint)i >= (uint)a.Length) throw new IndexOutOfRangeException();
            var et = a.GetType().GetElementType()!;
            switch (a)
            {
                case int[] ia: f.Push(ia[i]); return;
                case double[] da: f.Push(da[i]); return;
                case object?[] oa when !et.IsValueType: f.Push(oa[i]); return;
            }
            PushTyped(f, a.GetValue(i), et, copy: false);
        }

        static void StElem(Frame f, Type? t)
        {
            object? v = f.Pop();
            int i = AsI4(f.Pop());
            var a = (Array?)f.Pop() ?? throw new NullReferenceException();
            if ((uint)i >= (uint)a.Length) throw new IndexOutOfRangeException();
            var et = a.GetType().GetElementType()!;
            switch (a)
            {
                case int[] ia: ia[i] = AsI4(v); return;
                case double[] da: da[i] = AsR8(v); return;
            }
            if (!et.IsValueType && v != null && !et.IsInstanceOfType(v)) throw new ArrayTypeMismatchException();
            a.SetValue(FromStack(v, et), i);
        }

        // ---- calls ----

        void Call(Frame f, MethodBase m, bool virt, Type? constrained)
        {
            var ps = m.GetParametersAsSpan();
            var args = ps.Length == 0 ? Array.Empty<object?>() : new object?[ps.Length];
            Ref?[]? refs = null;
            for (int i = ps.Length - 1; i >= 0; i--)
            {
                var pt = ps[i].ParameterType;
                object? v = f.Pop();
                if (pt.IsByRef)
                {
                    var r = (Ref)v!;
                    (refs ??= new Ref?[ps.Length])[i] = r;
                    args[i] = r.Get();
                }
                else args[i] = FromStack(v, pt);
            }
            object? target = null;
            Ref? targetRef = null;
            if (!m.IsStatic)
            {
                object? t = f.Pop();
                if (t is Ref r)
                {
                    targetRef = r;
                    target = r.Get();
                    // a constrained call on a reference type: the pointer's object; on a value type: the value itself
                    if (constrained != null && !constrained.IsValueType) targetRef = null;
                }
                else target = t;
                if (target == null) throw new NullReferenceException();
            }
            object? result;
            if (m is DynamicMethod dm) result = dm.Invoke(null, BindingFlags.DoNotWrapExceptions, null, args, null);
            else if (m is ConstructorInfo ci) result = ci.Invoke(target, BindingFlags.DoNotWrapExceptions, null, args, null);
            else result = m.Invoke(target, BindingFlags.DoNotWrapExceptions, null, args, null);
            // a value-type receiver was changed in its box; by-ref arguments are written back
            if (targetRef != null && m.DeclaringType is { IsValueType: true }) targetRef.Set(target);
            if (refs != null)
                for (int i = 0; i < refs.Length; i++)
                    refs[i]?.Set(args[i]);
            if (m is MethodInfo mi && mi.ReturnType != typeof(void))
                PushTyped(f, result, mi.ReturnType, copy: false);
        }

        static void NewObj(Frame f, ConstructorInfo c)
        {
            var dt = c.DeclaringType!;
            var ps = c.GetParametersAsSpan();
            if (typeof(Delegate).IsAssignableFrom(dt) && ps.Length == 2 && f.Stack[f.Sp - 1] is MethodPointer mp)
            {
                f.Pop();
                object? target = f.Pop();
                var mi = (MethodInfo)mp.Method;
                f.Push(target == null || mi.IsStatic && target == null ? Delegate.CreateDelegate(dt, mi) : Delegate.CreateDelegate(dt, target, mi));
                return;
            }
            var args = ps.Length == 0 ? Array.Empty<object?>() : new object?[ps.Length];
            Ref?[]? refs = null;
            for (int i = ps.Length - 1; i >= 0; i--)
            {
                var pt = ps[i].ParameterType;
                object? v = f.Pop();
                if (pt.IsByRef) { var r = (Ref)v!; (refs ??= new Ref?[ps.Length])[i] = r; args[i] = r.Get(); }
                else args[i] = FromStack(v, pt);
            }
            object o = c.Invoke(BindingFlags.DoNotWrapExceptions, null, args, null);
            if (refs != null)
                for (int i = 0; i < refs.Length; i++)
                    refs[i]?.Set(args[i]);
            PushTyped(f, o, dt, copy: false);
        }

        // ---- values ----

        /// <summary>Pushes a value read from a location of type t, in its stack form (a value type's box copied when the
        /// location keeps it).</summary>
        static void PushTyped(Frame f, object? v, Type t, bool copy)
        {
            if (t.IsByRef || !t.IsValueType) { f.Push(v); return; }
            object? s = ToStack(v, t);
            if (s != null && ReferenceEquals(s, v) && !t.IsPrimitive && !t.IsEnum)
            {
                f.Push(copy ? Clone(v!) : v, true);
                return;
            }
            f.Push(s);
        }

        /// <summary>The stack form of a value of type t.</summary>
        static object? ToStack(object? v, Type t)
        {
            if (v == null || !t.IsValueType) return v;
            if (t == typeof(IntPtr)) return (int)(nint)v;
            if (t == typeof(UIntPtr)) return (int)(nuint)v;
            return Type.GetTypeCode(t) switch
            {
                TypeCode.Boolean => (bool)v ? 1 : 0,
                TypeCode.Char => (int)(char)v,
                TypeCode.SByte => (int)(sbyte)v,
                TypeCode.Byte => (int)(byte)v,
                TypeCode.Int16 => (int)(short)v,
                TypeCode.UInt16 => (int)(ushort)v,
                TypeCode.Int32 => (int)v,
                TypeCode.UInt32 => (int)(uint)v,
                TypeCode.Int64 => (long)v,
                TypeCode.UInt64 => (long)(ulong)v,
                TypeCode.Single => (double)(float)v,
                TypeCode.Double => (double)v,
                _ => v,
            };
        }

        /// <summary>A stack value as a value of type t (a box of t for a value type).</summary>
        static object? FromStack(object? s, Type t)
        {
            if (t.IsByRef) return s;
            if (!t.IsValueType) return s;
            if (t.IsEnum) return Enum.ToObject(t, AsI8(s));
            if (t == typeof(IntPtr)) return (nint)AsI4(s);
            if (t == typeof(UIntPtr)) return (nuint)(uint)AsI4(s);
            switch (Type.GetTypeCode(t))
            {
                case TypeCode.Boolean: return AsI4(s) != 0;
                case TypeCode.Char: return (char)AsI4(s);
                case TypeCode.SByte: return (sbyte)AsI4(s);
                case TypeCode.Byte: return (byte)AsI4(s);
                case TypeCode.Int16: return (short)AsI4(s);
                case TypeCode.UInt16: return (ushort)AsI4(s);
                case TypeCode.Int32: return AsI4(s);
                case TypeCode.UInt32: return (uint)AsI4(s);
                case TypeCode.Int64: return AsI8(s);
                case TypeCode.UInt64: return (ulong)AsI8(s);
                case TypeCode.Single: return (float)AsR8(s);
                case TypeCode.Double: return AsR8(s);
            }
            if (Nullable.GetUnderlyingType(t) != null) return s;
            return s ?? Default(t);
        }

        static object? Box(object? s, Type t)
        {
            if (Nullable.GetUnderlyingType(t) is { } u) return s == null ? null : FromStack(s, u);
            return FromStack(s, t);
        }

        static object? Default(Type t)
        {
            if (!t.IsValueType || t.IsByRef) return null;
            if (Nullable.GetUnderlyingType(t) != null) return null;
            return RuntimeHelpers.GetUninitializedObject(t);
        }

        static object? Copy(object? v, Type t) => v != null && t.IsValueType && !t.IsPrimitive && !t.IsEnum ? Clone(v) : v;

        static object Clone(object box) => box is string ? box : Object.CloneOf(box);

        static void CopyInto(object box, object value)
        {
            int id = Cs2Wasm.Runtime.Rt.TypeId(box);
            if ((Cs2Wasm.Runtime.TypeInfo.Flags2(id) & 1) != 0)
                throw new NotSupportedException("cs2wasm: storing through an unboxed pointer to a struct with references");
            int off = Cs2Wasm.Runtime.TypeInfo.BoxOffset(id);
            Cs2Wasm.Runtime.Mem.Copy(Cs2Wasm.Runtime.Rt.AddressOf(box) + off, Cs2Wasm.Runtime.Rt.AddressOf(value) + off, Cs2Wasm.Runtime.TypeInfo.Size(id));
        }

        static int SizeOf(Type t)
        {
            if (!t.IsValueType) return IntPtr.Size;
            return Cs2Wasm.Runtime.TypeInfo.Size(((RuntimeType)t).TypeId);
        }

        static bool IsInstance(object o, Type t)
        {
            if (t.IsInstanceOfType(o)) return true;
            // a box of an enum is a box of its underlying type and back (the CLR's unbox rule)
            var ot = o.GetType();
            if (t.IsValueType && ot.IsValueType && (t.IsEnum || ot.IsEnum))
                return (t.IsEnum ? Enum.GetUnderlyingType(t) : t) == (ot.IsEnum ? Enum.GetUnderlyingType(ot) : ot);
            return false;
        }

        static InvalidCastException CastError(object o, Type t) =>
            new InvalidCastException("Unable to cast object of type '" + o.GetType().FullName + "' to type '" + t.FullName + "'.");

        static int AsI4(object? s) => s switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            null => 0,
            _ => throw new InvalidProgramException(),
        };

        static long AsI8(object? s) => s switch
        {
            int i => i,
            long l => l,
            double d => (long)d,
            null => 0,
            Enum e => Convert.ToInt64(e),
            _ => throw new InvalidProgramException(),
        };

        static double AsR8(object? s) => s switch
        {
            double d => d,
            int i => i,
            long l => l,
            _ => throw new InvalidProgramException(),
        };

        // ---- arithmetic ----

        static object Binary(int op, object? a, object? b)
        {
            if (a is int x && b is int y)
            {
                return op switch
                {
                    0x58 => x + y, 0x59 => x - y, 0x5A => x * y, 0x5B => x / y, 0x5C => (int)((uint)x / (uint)y),
                    0x5D => x % y, 0x5E => (int)((uint)x % (uint)y), 0x5F => x & y, 0x60 => x | y, 0x61 => x ^ y,
                    0x62 => x << y, 0x63 => x >> y, _ => (int)((uint)x >> y),
                };
            }
            if (op >= 0x62)
            {
                int n = AsI4(b);
                long v = (long)a!;
                return op switch { 0x62 => v << n, 0x63 => v >> n, _ => (long)((ulong)v >> n) };
            }
            if (a is double || b is double)
            {
                double p = AsR8(a), q = AsR8(b);
                return op switch
                {
                    0x58 => p + q, 0x59 => p - q, 0x5A => p * q, 0x5B => p / q, 0x5D => p % q,
                    _ => throw new InvalidProgramException(),
                };
            }
            long l = AsI8(a), m = AsI8(b);
            return op switch
            {
                0x58 => l + m, 0x59 => l - m, 0x5A => l * m, 0x5B => l / m, 0x5C => (long)((ulong)l / (ulong)m),
                0x5D => l % m, 0x5E => (long)((ulong)l % (ulong)m), 0x5F => l & m, 0x60 => l | m, _ => l ^ m,
            };
        }

        static object Overflowing(int op, object? a, object? b)
        {
            if (a is int x && b is int y)
            {
                return op switch
                {
                    0xD6 => checked(x + y), 0xD7 => (int)checked((uint)x + (uint)y),
                    0xD8 => checked(x * y), 0xD9 => (int)checked((uint)x * (uint)y),
                    0xDA => checked(x - y), _ => (int)checked((uint)x - (uint)y),
                };
            }
            long l = AsI8(a), m = AsI8(b);
            return op switch
            {
                0xD6 => checked(l + m), 0xD7 => (long)checked((ulong)l + (ulong)m),
                0xD8 => checked(l * m), 0xD9 => (long)checked((ulong)l * (ulong)m),
                0xDA => checked(l - m), _ => (long)checked((ulong)l - (ulong)m),
            };
        }

        static object Conv(object? s, TypeCode to)
        {
            switch (s)
            {
                case int i:
                    return to switch
                    {
                        TypeCode.SByte => (int)(sbyte)i, TypeCode.Byte => (int)(byte)i, TypeCode.Int16 => (int)(short)i,
                        TypeCode.UInt16 => (int)(ushort)i, TypeCode.Int32 or TypeCode.UInt32 => i,
                        TypeCode.Int64 => (long)i, TypeCode.UInt64 => (long)(uint)i,
                        TypeCode.Single => (double)(float)i, _ => (double)i,
                    };
                case long l:
                    return to switch
                    {
                        TypeCode.SByte => (int)(sbyte)l, TypeCode.Byte => (int)(byte)l, TypeCode.Int16 => (int)(short)l,
                        TypeCode.UInt16 => (int)(ushort)l, TypeCode.Int32 or TypeCode.UInt32 => (int)l,
                        TypeCode.Int64 or TypeCode.UInt64 => l,
                        TypeCode.Single => (double)(float)l, _ => (double)l,
                    };
                case double d:
                    return to switch
                    {
                        TypeCode.SByte => (int)(sbyte)d, TypeCode.Byte => (int)(byte)d, TypeCode.Int16 => (int)(short)d,
                        TypeCode.UInt16 => (int)(ushort)d, TypeCode.Int32 => (int)d, TypeCode.UInt32 => (int)(uint)d,
                        TypeCode.Int64 => (long)d, TypeCode.UInt64 => (long)(ulong)d,
                        TypeCode.Single => (double)(float)d, _ => d,
                    };
            }
            throw new InvalidProgramException();
        }

        // conv.ovf.{i1,u1,i2,u2,i4,u4,i8,u8} (0..7); conv.ovf.i / .u are 4 / 5 on a 32-bit target
        static object ConvOvf(object? s, int k)
        {
            checked
            {
                switch (s)
                {
                    case int i:
                        return k switch { 0 => (int)(sbyte)i, 1 => (int)(byte)i, 2 => (int)(short)i, 3 => (int)(ushort)i, 4 => i, 5 => (int)(uint)i, 6 => (long)i, _ => (long)(ulong)i };
                    case long l:
                        return k switch { 0 => (int)(sbyte)l, 1 => (int)(byte)l, 2 => (int)(short)l, 3 => (int)(ushort)l, 4 => (int)l, 5 => unchecked((int)(uint)l), 6 => l, _ => unchecked((long)(ulong)l) };
                    case double d:
                        return k switch { 0 => (int)(sbyte)d, 1 => (int)(byte)d, 2 => (int)(short)d, 3 => (int)(ushort)d, 4 => (int)d, 5 => unchecked((int)(uint)d), 6 => (long)d, _ => unchecked((long)(ulong)d) };
                }
            }
            throw new InvalidProgramException();
        }

        // conv.ovf.{i1,i2,i4,i8,u1,u2,u4,u8,i,u}.un (0..9): the source is unsigned
        static object ConvOvfUn(object? s, int k)
        {
            ulong u = s switch { int i => (uint)i, long l => (ulong)l, double d => checked((ulong)d), _ => throw new InvalidProgramException() };
            checked
            {
                return k switch
                {
                    0 => (int)(sbyte)u, 1 => (int)(short)u, 2 => (int)u, 3 => (long)u,
                    4 => (int)(byte)u, 5 => (int)(ushort)u, 6 => unchecked((int)(uint)u), 7 => unchecked((long)u),
                    8 => (int)u, _ => unchecked((int)(uint)u),
                };
            }
        }

        // kinds: 0 eq, 1 ne.un, 2 ge, 3 gt, 4 le, 5 lt, 6 ge.un, 7 gt.un... in branch order (beq..blt.un: 0..9 minus the
        // brfalse/brtrue/br prefix), with cgt.un = 8 and clt.un = 10 mapped below
        static bool Compare(int kind, object? a, object? b)
        {
            if (a is int x && b is int y)
            {
                return kind switch
                {
                    0 => x == y, 1 => x != y, 2 => x >= y, 3 => x > y, 4 => x <= y, 5 => x < y,
                    6 => (uint)x >= (uint)y, 7 or 8 => (uint)x > (uint)y, 9 => (uint)x <= (uint)y, _ => (uint)x < (uint)y,
                };
            }
            if (a is double || b is double)
            {
                double p = AsR8(a), q = AsR8(b);
                return kind switch
                {
                    0 => p == q, 1 => !(p == q), 2 => p >= q, 3 => p > q, 4 => p <= q, 5 => p < q,
                    6 => !(p < q), 7 or 8 => !(p <= q), 9 => !(p > q), _ => !(p >= q),
                };
            }
            if (a is int or long && b is int or long)
            {
                long l = AsI8(a), m = AsI8(b);
                return kind switch
                {
                    0 => l == m, 1 => l != m, 2 => l >= m, 3 => l > m, 4 => l <= m, 5 => l < m,
                    6 => (ulong)l >= (ulong)m, 7 or 8 => (ulong)l > (ulong)m, 9 => (ulong)l <= (ulong)m, _ => (ulong)l < (ulong)m,
                };
            }
            // object references: equality, and cgt.un against null (`x != null`)
            bool same = ReferenceEquals(a, b) || a is Ref ra && b is Ref rb && ReferenceEquals(ra.Get(), rb.Get());
            return kind switch
            {
                0 => same,
                1 => !same,
                7 or 8 => !same && b == null,
                _ => throw new InvalidProgramException(),
            };
        }

        // short/long branch index: 0 br, 1 brfalse, 2 brtrue, 3 beq, 4 bge, 5 bgt, 6 ble, 7 blt, 8 bne.un, 9 bge.un,
        // 10 bgt.un, 11 ble.un, 12 blt.un
        static bool Branch(Frame f, int k)
        {
            switch (k)
            {
                case 0: return true;
                case 1: return !Truthy(f.Pop());
                case 2: return Truthy(f.Pop());
            }
            object? b = f.Pop(); object? a = f.Pop();
            return k switch
            {
                3 => Compare(0, a, b), 4 => Compare(2, a, b), 5 => Compare(3, a, b), 6 => Compare(4, a, b), 7 => Compare(5, a, b),
                8 => Compare(1, a, b), 9 => Compare(6, a, b), 10 => Compare(7, a, b), 11 => Compare(9, a, b), _ => Compare(10, a, b),
            };
        }

        static bool Truthy(object? v) => v switch
        {
            null => false,
            int i => i != 0,
            long l => l != 0,
            double d => d != 0,
            _ => true,
        };
    }
}

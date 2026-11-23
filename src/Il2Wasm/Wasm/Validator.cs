namespace Il2Wasm.Wasm;

/// <summary>
/// Type-checks function bodies as a WASM engine does (operand stack and control stack), before encoding, so a code
/// generation bug is reported with the function and the instruction rather than as an engine's compile error on the
/// first bad function. Run on every build when IL2WASM_VALIDATE=1 (and by the tests).
/// </summary>
internal static class Validator
{
    const ValType V128 = (ValType)0x7B;

    sealed class Frame
    {
        public required Op Kind;
        public required ValType[] Params, Results;
        public int Height;
        public bool Unreachable;
        public ValType[] Labels => Kind == Op.Loop ? Params : Results;
    }

    /// <summary>The first problem in the function, or null.</summary>
    public static string? Check(WasmFunction f, IReadOnlyList<WasmGlobal> globals)
    {
        var locals = f.Type.Params.Concat(f.Locals).ToArray();
        var stack = new List<ValType?>(); // null: unknown (after unreachable code)
        var ctl = new List<Frame> { new() { Kind = Op.Block, Params = [], Results = f.Type.Results, Height = 0 } };
        int i = 0;
        string Where(string msg) => $"{f.Name}: instruction {i} ({(i >= 0 && i < f.Body.Count ? f.Body[i].ToString() : "end")}): {msg}";

        ValType? Pop(ValType? want)
        {
            var fr = ctl[^1];
            if (stack.Count == fr.Height)
            {
                if (fr.Unreachable) return want;
                throw new InvalidOperationException($"the operand stack is empty (wanted {want?.ToString() ?? "a value"})");
            }
            var t = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (want != null && t != null && t != want) throw new InvalidOperationException($"type mismatch: wanted {want}, got {t}");
            return t ?? want;
        }
        void PopN(IReadOnlyList<ValType> ts) { for (int k = ts.Count - 1; k >= 0; k--) Pop(ts[k]); }
        void Push(ValType t) => stack.Add(t);
        void PushN(IEnumerable<ValType> ts) { foreach (var t in ts) stack.Add(t); }
        void Unreach() { var fr = ctl[^1]; stack.RemoveRange(fr.Height, stack.Count - fr.Height); fr.Unreachable = true; }
        (ValType[] p, ValType[] r) BlockSig(object? bt) => bt switch
        {
            null => ([], []),
            ValType v => ([], [v]),
            FuncType ft => (ft.Params, ft.Results),
            _ => throw new InvalidOperationException("bad block type"),
        };
        Frame Target(long depth)
        {
            if (depth < 0 || depth >= ctl.Count) throw new InvalidOperationException($"branch depth {depth} out of range ({ctl.Count} frames)");
            return ctl[ctl.Count - 1 - (int)depth];
        }
        void Unop(ValType a, ValType r) { Pop(a); Push(r); }
        void Binop(ValType a, ValType r) { Pop(a); Pop(a); Push(r); }

        try
        {
            for (i = 0; i < f.Body.Count; i++)
            {
                var ins = f.Body[i];
                var op = ins.Op;
                switch (op)
                {
                    case Op.Unreachable: Unreach(); break;
                    case Op.Nop: break;
                    case Op.Block: case Op.Loop:
                        {
                            var (p, r) = BlockSig(ins.Ref);
                            PopN(p);
                            ctl.Add(new Frame { Kind = op, Params = p, Results = r, Height = stack.Count });
                            PushN(p);
                            break;
                        }
                    case Op.If:
                        {
                            Pop(ValType.I32);
                            var (p, r) = BlockSig(ins.Ref);
                            PopN(p);
                            ctl.Add(new Frame { Kind = op, Params = p, Results = r, Height = stack.Count });
                            PushN(p);
                            break;
                        }
                    case Op.TryTable:
                        {
                            var tti = (TryTableInfo)ins.Ref!;
                            var (p, r) = BlockSig(tti.BlockType);
                            PopN(p);
                            ctl.Add(new Frame { Kind = op, Params = p, Results = r, Height = stack.Count });
                            PushN(p);
                            break;
                        }
                    case Op.Else:
                        {
                            var fr = ctl[^1];
                            if (fr.Kind != Op.If) throw new InvalidOperationException("else outside if");
                            PopN(fr.Results);
                            if (stack.Count != fr.Height) throw new InvalidOperationException($"{stack.Count - fr.Height} values left at else");
                            ctl[^1] = new Frame { Kind = Op.Else, Params = fr.Params, Results = fr.Results, Height = fr.Height };
                            PushN(fr.Params);
                            break;
                        }
                    case Op.End:
                        {
                            var fr = ctl[^1];
                            PopN(fr.Results);
                            if (stack.Count != fr.Height) throw new InvalidOperationException($"{stack.Count - fr.Height} values left at end of {fr.Kind}");
                            if (fr.Kind == Op.If && !fr.Params.SequenceEqual(fr.Results)) throw new InvalidOperationException("if without else must leave its parameters");
                            ctl.RemoveAt(ctl.Count - 1);
                            if (ctl.Count == 0) throw new InvalidOperationException("end of the function's own block");
                            PushN(fr.Results);
                            break;
                        }
                    case Op.Br: PopN(Target(ins.Imm).Labels); Unreach(); break;
                    case Op.BrIf: { Pop(ValType.I32); var l = Target(ins.Imm).Labels; PopN(l); PushN(l); break; }
                    case Op.BrTable:
                        {
                            Pop(ValType.I32);
                            var d = Target(ins.Imm).Labels;
                            foreach (var t in (int[])ins.Ref!)
                                if (Target(t).Labels.Length != d.Length) throw new InvalidOperationException("br_table targets of different arity");
                            PopN(d);
                            Unreach();
                            break;
                        }
                    case Op.Return: PopN(f.Type.Results); Unreach(); break;
                    case Op.Call:
                        {
                            var callee = (WasmFunction)ins.Ref!;
                            PopN(callee.Type.Params); PushN(callee.Type.Results);
                            break;
                        }
                    case Op.CallIndirect:
                        {
                            var ft = (FuncType)ins.Ref!;
                            Pop(ValType.I32); PopN(ft.Params); PushN(ft.Results);
                            break;
                        }
                    case Op.Throw:
                        {
                            var tag = (WasmTag)ins.Ref!;
                            PopN(tag.Type.Params); Unreach();
                            break;
                        }
                    case Op.ThrowRef: Pop(ValType.ExnRef); Unreach(); break;
                    case Op.Drop: Pop(null); break;
                    case Op.Select:
                        {
                            Pop(ValType.I32);
                            var b = Pop(null); var a = Pop(b);
                            if (a != null) Push(a.Value); else stack.Add(null);
                            break;
                        }
                    case Op.LocalGet: Push(Local(locals, ins.Imm)); break;
                    case Op.LocalSet: Pop(Local(locals, ins.Imm)); break;
                    case Op.LocalTee: { var t = Local(locals, ins.Imm); Pop(t); Push(t); break; }
                    case Op.GlobalGet: Push(((WasmGlobal)ins.Ref!).Type); break;
                    case Op.GlobalSet: Pop(((WasmGlobal)ins.Ref!).Type); break;
                    case Op.I32Load or Op.I32Load8S or Op.I32Load8U or Op.I32Load16S or Op.I32Load16U: Unop(ValType.I32, ValType.I32); break;
                    case Op.I64Load or (>= Op.I64Load8S and <= Op.I64Load32U): Unop(ValType.I32, ValType.I64); break;
                    case Op.F32Load: Unop(ValType.I32, ValType.F32); break;
                    case Op.F64Load: Unop(ValType.I32, ValType.F64); break;
                    case Op.I32Store or Op.I32Store8 or Op.I32Store16: Pop(ValType.I32); Pop(ValType.I32); break;
                    case Op.I64Store or Op.I64Store8 or Op.I64Store16 or Op.I64Store32: Pop(ValType.I64); Pop(ValType.I32); break;
                    case Op.F32Store: Pop(ValType.F32); Pop(ValType.I32); break;
                    case Op.F64Store: Pop(ValType.F64); Pop(ValType.I32); break;
                    case Op.MemorySize: Push(ValType.I32); break;
                    case Op.MemoryGrow: Unop(ValType.I32, ValType.I32); break;
                    case Op.I32Const: Push(ValType.I32); break;
                    case Op.I64Const: Push(ValType.I64); break;
                    case Op.F32Const: Push(ValType.F32); break;
                    case Op.F64Const: Push(ValType.F64); break;
                    case Op.I32Eqz: Unop(ValType.I32, ValType.I32); break;
                    case >= Op.I32Eq and <= Op.I32GeU: Binop(ValType.I32, ValType.I32); break;
                    case Op.I64Eqz: Unop(ValType.I64, ValType.I32); break;
                    case >= Op.I64Eq and <= Op.I64GeU: Binop(ValType.I64, ValType.I32); break;
                    case >= Op.F32Eq and <= Op.F32Ge: Binop(ValType.F32, ValType.I32); break;
                    case >= Op.F64Eq and <= Op.F64Ge: Binop(ValType.F64, ValType.I32); break;
                    case Op.I32Clz or Op.I32Ctz or Op.I32Popcnt: Unop(ValType.I32, ValType.I32); break;
                    case >= Op.I32Add and <= Op.I32Rotr: Binop(ValType.I32, ValType.I32); break;
                    case Op.I64Clz or Op.I64Ctz or Op.I64Popcnt: Unop(ValType.I64, ValType.I64); break;
                    case >= Op.I64Add and <= Op.I64Rotr: Binop(ValType.I64, ValType.I64); break;
                    case >= Op.F32Abs and <= Op.F32Sqrt: Unop(ValType.F32, ValType.F32); break;
                    case >= Op.F32Add and <= Op.F32Copysign: Binop(ValType.F32, ValType.F32); break;
                    case >= Op.F64Abs and <= Op.F64Sqrt: Unop(ValType.F64, ValType.F64); break;
                    case >= Op.F64Add and <= Op.F64Copysign: Binop(ValType.F64, ValType.F64); break;
                    case Op.I32WrapI64: Unop(ValType.I64, ValType.I32); break;
                    case Op.I32TruncF32S or Op.I32TruncF32U: Unop(ValType.F32, ValType.I32); break;
                    case Op.I32TruncF64S or Op.I32TruncF64U: Unop(ValType.F64, ValType.I32); break;
                    case Op.I64ExtendI32S or Op.I64ExtendI32U: Unop(ValType.I32, ValType.I64); break;
                    case Op.I64TruncF32S or Op.I64TruncF32U: Unop(ValType.F32, ValType.I64); break;
                    case Op.I64TruncF64S or Op.I64TruncF64U: Unop(ValType.F64, ValType.I64); break;
                    case Op.F32ConvertI32S or Op.F32ConvertI32U: Unop(ValType.I32, ValType.F32); break;
                    case Op.F32ConvertI64S or Op.F32ConvertI64U: Unop(ValType.I64, ValType.F32); break;
                    case Op.F32DemoteF64: Unop(ValType.F64, ValType.F32); break;
                    case Op.F64ConvertI32S or Op.F64ConvertI32U: Unop(ValType.I32, ValType.F64); break;
                    case Op.F64ConvertI64S or Op.F64ConvertI64U: Unop(ValType.I64, ValType.F64); break;
                    case Op.F64PromoteF32: Unop(ValType.F32, ValType.F64); break;
                    case Op.I32ReinterpretF32: Unop(ValType.F32, ValType.I32); break;
                    case Op.I64ReinterpretF64: Unop(ValType.F64, ValType.I64); break;
                    case Op.F32ReinterpretI32: Unop(ValType.I32, ValType.F32); break;
                    case Op.F64ReinterpretI64: Unop(ValType.I64, ValType.F64); break;
                    case Op.I32Extend8S or Op.I32Extend16S: Unop(ValType.I32, ValType.I32); break;
                    case Op.I64Extend8S or Op.I64Extend16S or Op.I64Extend32S: Unop(ValType.I64, ValType.I64); break;
                    case Op.I32TruncSatF32S or Op.I32TruncSatF32U: Unop(ValType.F32, ValType.I32); break;
                    case Op.I32TruncSatF64S or Op.I32TruncSatF64U: Unop(ValType.F64, ValType.I32); break;
                    case Op.I64TruncSatF32S or Op.I64TruncSatF32U: Unop(ValType.F32, ValType.I64); break;
                    case Op.I64TruncSatF64S or Op.I64TruncSatF64U: Unop(ValType.F64, ValType.I64); break;
                    case Op.MemoryInit or Op.MemoryCopy or Op.MemoryFill: Pop(ValType.I32); Pop(ValType.I32); Pop(ValType.I32); break;
                    case Op.DataDrop: break;
                    case Op.MemoryAtomicNotify: Binop(ValType.I32, ValType.I32); break;
                    case Op.MemoryAtomicWait32: Pop(ValType.I64); Pop(ValType.I32); Pop(ValType.I32); Push(ValType.I32); break;
                    case Op.MemoryAtomicWait64: Pop(ValType.I64); Pop(ValType.I64); Pop(ValType.I32); Push(ValType.I32); break;
                    case Op.AtomicFence: break;
                    case Op.I32AtomicLoad: Unop(ValType.I32, ValType.I32); break;
                    case Op.I64AtomicLoad: Unop(ValType.I32, ValType.I64); break;
                    case Op.I32AtomicStore: Pop(ValType.I32); Pop(ValType.I32); break;
                    case Op.I64AtomicStore: Pop(ValType.I64); Pop(ValType.I32); break;
                    case Op.I32AtomicRmwAdd or Op.I32AtomicRmwSub or Op.I32AtomicRmwXchg: Binop(ValType.I32, ValType.I32); break;
                    case Op.I64AtomicRmwAdd or Op.I64AtomicRmwSub or Op.I64AtomicRmwXchg: Pop(ValType.I64); Pop(ValType.I32); Push(ValType.I64); break;
                    case Op.I32AtomicRmwCmpxchg or Op.I32AtomicRmw8CmpxchgU or Op.I32AtomicRmw16CmpxchgU: Pop(ValType.I32); Pop(ValType.I32); Pop(ValType.I32); Push(ValType.I32); break;
                    case Op.I32AtomicRmw8XchgU or Op.I32AtomicRmw16XchgU: Binop(ValType.I32, ValType.I32); break;
                    case Op.I64AtomicRmwCmpxchg: Pop(ValType.I64); Pop(ValType.I64); Pop(ValType.I32); Push(ValType.I64); break;
                    // 128-bit instructions: their values are on the stack only (V128 below is the validator's own mark)
                    case Op.V128Load: Pop(ValType.I32); Push(V128); break;
                    case Op.V128Store: Pop(V128); Pop(ValType.I32); break;
                    case Op.I16x8Splat: Pop(ValType.I32); Push(V128); break;
                    case Op.F64x2Splat: Pop(ValType.F64); Push(V128); break;
                    case Op.F64x2ExtractLane: Pop(V128); Push(ValType.F64); break;
                    case Op.F64x2Sqrt: Pop(V128); Push(V128); break;
                    case Op.I16x8Bitmask: Pop(V128); Push(ValType.I32); break;
                    case Op.I16x8Eq or Op.I16x8LtU or Op.I16x8GtU or Op.V128And or Op.V128Or or Op.I16x8Sub or Op.I16x8Add: Pop(V128); Pop(V128); Push(V128); break;
                    default: throw new InvalidOperationException("unknown instruction");
                }
            }
            // the body's own end is the encoder's
            if (ctl.Count != 1) return $"{f.Name}: {ctl.Count - 1} blocks not ended";
            i = f.Body.Count - 1;
            PopN(f.Type.Results);
            if (stack.Count != 0) return $"{f.Name}: {stack.Count} values left at the end";
        }
        catch (InvalidOperationException ex)
        {
            return Where(ex.Message);
        }
        return null;
    }

    static ValType Local(ValType[] locals, long i) =>
        i >= 0 && i < locals.Length ? locals[i] : throw new InvalidOperationException($"local {i} out of range ({locals.Length})");
}

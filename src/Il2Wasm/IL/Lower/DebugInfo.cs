#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using Il2Wasm.Wasm;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

namespace Il2Wasm.IL.Lower;

// Stack traces (SPEC 6.3) as the operation-tree front end made them (Compiler/Frames.cs, RuntimeGen.Emit), from IL
// metadata and the portable PDB:
//   - each compiled method is a frame named in .NET's StackTrace format ("NS.Type`1.Method[T](Int32 x, String& s)"),
//     except those .NET leaves out ([StackTraceHidden] on it or a type around it, a delegate's Invoke) and the
//     runtime's own (Il2Wasm.Runtime);
//   - every instruction has the file and line of the sequence point at or before its IL offset (hidden ones: none),
//     for the program's own assemblies (those with a PDB, not the core library: .NET shows no line for its library);
//   - after encoding, the il2wasm.frames section (which functions are frames, the library's frame names) and the
//     il2wasm.lines section (code offset to file and line) the host reads when a trace is taken (host/engine.mjs);
//   - a throw records the trace (Rt.OnThrow, through rt.throw_hook) and a catch that takes the exception cuts it at
//     its own frame (Rt.OnCatch).
public sealed partial class IlCompiler
{
    readonly List<string> _files = [];
    readonly Dictionary<string, int> _fileIndex = new();
    readonly List<SourcePos> _positions = [];
    readonly Dictionary<(int, int), int> _posIndex = new();
    readonly Dictionary<EcmaModule, MetadataReader?> _pdbs = new();
    readonly List<MetadataReaderProvider> _pdbProviders = [];
    readonly Dictionary<MethodDesc, (int[] offsets, int[] pos)?> _seqPoints = new();
    WasmFunction? _onCatch;

    MetadataReader? PdbOf(EcmaModule m)
    {
        if (_pdbs.TryGetValue(m, out var r)) return r;
        r = null;
        // (the core library's lines are not shown, as .NET shows none for its own)
        if (m != _ctx.SystemModule)
        {
            var name = m.MetadataReader.GetString(m.MetadataReader.GetAssemblyDefinition().Name);
            if (_ctx is Types.IlContext ic && ic.PathOf(name) is { } path && File.Exists(Path.ChangeExtension(path, ".pdb")))
            {
                try
                {
                    var p = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(Path.ChangeExtension(path, ".pdb")));
                    _pdbProviders.Add(p);
                    r = p.GetMetadataReader();
                }
                catch (BadImageFormatException) { r = null; }
            }
        }
        _pdbs[m] = r;
        return r;
    }

    /// <summary>Whether a method's module is the program's own (it has a PDB): its frames are named in the name section
    /// and its lines shown.</summary>
    bool IsProgramMethod(MethodDesc m) => m.GetTypicalMethodDefinition() is EcmaMethod em && PdbOf(em.Module) != null;

    /// <summary>The position (an index into the line table) of an IL offset of a method, or -1.</summary>
    internal int PositionOf(MethodDesc m, int ilOffset)
    {
        if (!_seqPoints.TryGetValue(m, out var sp))
        {
            sp = null;
            if (m.GetTypicalMethodDefinition() is EcmaMethod em && PdbOf(em.Module) is { } pdb)
            {
                var offs = new List<int>(); var pos = new List<int>();
                try
                {
                    var info = pdb.GetMethodDebugInformation(em.Handle.ToDebugInformationHandle());
                    foreach (var s in info.GetSequencePoints())
                    {
                        offs.Add(s.Offset);
                        if (s.IsHidden) { pos.Add(-1); continue; }
                        var file = pdb.GetString(pdb.GetDocument(s.Document).Name);
                        if (!_fileIndex.TryGetValue(file, out int fi)) { fi = _files.Count; _files.Add(file); _fileIndex[file] = fi; }
                        if (!_posIndex.TryGetValue((fi, s.StartLine), out int pi)) { pi = _positions.Count; _positions.Add(new SourcePos(fi, s.StartLine)); _posIndex[(fi, s.StartLine)] = pi; }
                        pos.Add(pi);
                    }
                }
                catch (BadImageFormatException) { }
                if (offs.Count > 0) sp = (offs.ToArray(), pos.ToArray());
            }
            _seqPoints[m] = sp;
        }
        if (sp is not { } t) return -1;
        int i = Array.BinarySearch(t.offsets, ilOffset);
        if (i < 0) i = ~i - 1;
        return i < 0 ? -1 : t.pos[i];
    }

    /// <summary>A method's function: its frame name (null: no frame of a trace) and whether it is the program's.</summary>
    internal void SetFrame(WasmFunction f, MethodDesc m)
    {
        if (IsHiddenFrame(m)) return;
        f.FrameName = FrameName(m);
        f.ProgramCode = IsProgramMethod(m);
    }

    static bool HasHidden(EcmaMethod m) => m.HasCustomAttribute("System.Diagnostics", "StackTraceHiddenAttribute");

    static bool IsHiddenFrame(MethodDesc m)
    {
        if (m.GetTypicalMethodDefinition() is not EcmaMethod em) return true;
        if (m.OwningType.IsDelegate && m.GetName() == "Invoke") return true;
        if (HasHidden(em)) return true;
        for (var t = (MetadataType?)em.OwningType; t != null; t = t.ContainingType)
        {
            if (t is EcmaType et && et.HasCustomAttribute("System.Diagnostics", "StackTraceHiddenAttribute")) return true;
            if (t.ContainingType == null && t.GetNamespace().StartsWith("Il2Wasm.Runtime", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>"Namespace.Type`1.Method[T](Int32 x, String&amp; s)" from the method's definition.</summary>
    static string FrameName(MethodDesc m)
    {
        var def = (EcmaMethod)m.GetTypicalMethodDefinition();
        var sb = new StringBuilder();
        AppendTypeFull(sb, (MetadataType)def.OwningType);
        sb.Append('.').Append(def.GetName());
        if (def.Instantiation.Length > 0) sb.Append('[').Append(string.Join(",", Enumerable.Range(0, def.Instantiation.Length).Select(i => ((GenericParameterDesc)def.Instantiation[i]).Name))).Append(']');
        sb.Append('(');
        var sig = def.Signature;
        var names = new string[sig.Length];
        var md = def.MetadataReader;
        foreach (var ph in md.GetMethodDefinition(def.Handle).GetParameters())
        {
            var p = md.GetParameter(ph);
            if (p.SequenceNumber >= 1 && p.SequenceNumber <= sig.Length) names[p.SequenceNumber - 1] = md.GetString(p.Name);
        }
        for (int i = 0; i < sig.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(ShortTypeName(sig[i], def));
            sb.Append(" ").Append(names[i] ?? "");
        }
        sb.Append(')');
        return sb.ToString();
    }

    static void AppendTypeFull(StringBuilder sb, MetadataType t)
    {
        if (t.ContainingType != null) { AppendTypeFull(sb, t.ContainingType); sb.Append('.'); }
        else if (t.GetNamespace().Length > 0) sb.Append(t.GetNamespace()).Append('.');
        sb.Append(t.GetName());
    }

    static string ShortTypeName(TypeDesc t, MethodDesc def) => t switch
    {
        ArrayType a => ShortTypeName(a.ElementType, def) + "[" + new string(',', a.Rank - 1) + "]",
        PointerType p => ShortTypeName(p.ParameterType, def) + "*",
        ByRefType r => ShortTypeName(r.ParameterType, def) + "&",
        SignatureMethodVariable v when v.Index < def.Instantiation.Length => ((GenericParameterDesc)def.Instantiation[v.Index]).Name,
        SignatureTypeVariable v when v.Index < def.OwningType.Instantiation.Length => ((GenericParameterDesc)def.OwningType.Instantiation[v.Index]).Name,
        GenericParameterDesc g => g.Name,
        MetadataType n => ((MetadataType)n.GetTypeDefinition()).GetName(),
        _ => t.ToString()!,
    };

    /// <summary>(ex): a catch took the exception: its trace ends at this frame.</summary>
    internal WasmFunction OnCatchFn => _onCatch ??= TryCoreMethod("Il2Wasm.Runtime", "Rt", "OnCatch") is { } oc ? FunctionFor(oc) : Module.AddFunction("rt.on_catch", new FuncType([ValType.I32], []));

    /// <summary>The encoded module with the frames and lines sections after it.</summary>
    byte[] WithDebugSections(WasmModule.Encoded enc)
    {
        var fr = new Writer();
        int nfn = enc.FunctionOrder.Count == 0 ? 0 : enc.FunctionOrder.Max(f => f.Index) + 1;
        fr.U32(nfn);
        var bits = new byte[(nfn + 7) / 8];
        foreach (var f in enc.FunctionOrder) if (f.FrameName != null) bits[f.Index >> 3] |= (byte)(1 << (f.Index & 7));
        fr.Bytes(bits);
        var strings = new List<string>();
        var stringIndex = new Dictionary<string, int>();
        int Str(string s) { if (!stringIndex.TryGetValue(s, out int i)) { i = strings.Count; strings.Add(s); stringIndex[s] = i; } return i; }
        var framesW = new Writer();
        var libFrames = enc.FunctionOrder.Where(f => f.FrameName != null && !f.ProgramCode).ToList();
        framesW.U32(libFrames.Count);
        foreach (var f in libFrames)
        {
            framesW.U32(f.Index);
            var name = f.FrameName!;
            int paren = name.IndexOf('(');
            int gen = name.IndexOf('[');
            int head = paren < 0 ? -1 : name.LastIndexOf('.', gen >= 0 && gen < paren ? gen : paren - 1);
            if (paren < 0 || head < 0 || !name.EndsWith(')')) { framesW.U32(0); framesW.U32(Str(name)); continue; }
            var ps = name[(paren + 1)..^1];
            var parts = ps.Length == 0 ? [] : ps.Split(", ");
            if (parts.Any(p => p.LastIndexOf(' ') <= 0)) { framesW.U32(0); framesW.U32(Str(name)); continue; }
            framesW.U32(parts.Length + 1);
            framesW.U32(Str(name[..head])); framesW.U32(Str(name[(head + 1)..paren]));
            foreach (var p in parts) { int sp = p.LastIndexOf(' '); framesW.U32(Str(p[..sp])); framesW.U32(Str(p[(sp + 1)..])); }
        }
        fr.U32(strings.Count); foreach (var s in strings) fr.Name(s);
        fr.Bytes(framesW.ToArray());
        var lenders = enc.FunctionOrder.Where(f => f.LendsLines).Select(f => f.Index).Order().ToList();
        if (lenders.Count > 0) { fr.U32(lenders.Count); int last = 0; foreach (var i in lenders) { fr.U32(i - last); last = i; } }
        // lines: (offset delta, line delta << 1 | file changes, the file when it changes); a position of none (-1
        // as the file count) where the program's code gives way to code without lines
        var entries = new List<(int off, int file, int line)>();
        foreach (var l in enc.Lines.OrderBy(l => l.Offset))
        {
            if (l.Pos < 0 || l.Pos >= _positions.Count) { if (entries.Count > 0 && entries[^1].file != -1) entries.Add((l.Offset, -1, 0)); continue; }
            var p = _positions[l.Pos];
            if (entries.Count > 0 && entries[^1].file == p.File && entries[^1].line == p.Line) continue;
            entries.Add((l.Offset, p.File, p.Line));
        }
        var lt = new Writer();
        lt.U32(_files.Count); foreach (var f in _files) lt.Name(f);
        lt.U32(entries.Count);
        int lastOff = 0, lastFile = -1, lastLine = 0;
        foreach (var (off, file, line) in entries)
        {
            lt.U32(off - lastOff);
            bool fileChanges = file != lastFile;
            lt.S32(((line - lastLine) << 1) | (fileChanges ? 1 : 0));
            if (fileChanges) lt.U32(file < 0 ? _files.Count : file);
            lastOff = off; lastFile = file; lastLine = line;
        }
        var w = new Writer();
        w.Bytes(enc.Bytes);
        var sec = new Writer(); sec.Name("il2wasm.lines"); sec.Bytes(lt.ToArray());
        w.Byte(0); w.U32(sec.Length); w.Bytes(sec.ToArray());
        var frRaw = fr.ToArray();
        var frPacked = DataCompression.Compress(frRaw);
        var fsec = new Writer(); fsec.Name("il2wasm.frames");
        if (frPacked.Length + 8 < frRaw.Length) { fsec.Byte(1); fsec.U32(frRaw.Length); fsec.Bytes(frPacked); }
        else { fsec.Byte(0); fsec.Bytes(frRaw); }
        w.Byte(0); w.U32(fsec.Length); w.Bytes(fsec.ToArray());
        // inlined calls: for a call or a throw in inlined code, the frames a trace shows there besides the function's
        // own (names; the files; the scopes: parent + 1 or 0, name, file + 1 or 0, line of the call; then (offset
        // delta, scope + 1) in offset order)
        if (enc.Inlines.Count > 0)
        {
            var scopeIds = new Dictionary<int, int>();
            var rows = new List<(int parent, int name, int file, int line)>();
            var inNames = new List<string>(); var inNameIx = new Dictionary<string, int>();
            int ScopeId(int s)
            {
                if (s == 0) return -1;
                if (scopeIds.TryGetValue(s, out int id)) return id;
                var (parent, fn, pos) = Module.InlineScopes[s];
                int pid = ScopeId(parent);
                string nm = fn.FrameName ?? fn.Name;
                if (!inNameIx.TryGetValue(nm, out int ni)) { ni = inNames.Count; inNames.Add(nm); inNameIx[nm] = ni; }
                int fi = -1, line = 0;
                if (pos >= 0 && pos < _positions.Count) { fi = _positions[pos].File; line = _positions[pos].Line; }
                rows.Add((pid, ni, fi, line));
                return scopeIds[s] = rows.Count - 1;
            }
            var ents = enc.Inlines.OrderBy(x => x.Offset).Select(x => (x.Offset, id: ScopeId(x.Scope))).ToList();
            var iw = new Writer();
            iw.U32(inNames.Count); foreach (var s in inNames) iw.Name(s);
            iw.U32(_files.Count); foreach (var s in _files) iw.Name(s);
            iw.U32(rows.Count);
            foreach (var (parent, name, file, line) in rows) { iw.U32(parent + 1); iw.U32(name); iw.U32(file + 1); iw.U32(line); }
            iw.U32(ents.Count);
            int lastAt = 0;
            foreach (var (off, id) in ents) { iw.U32(off - lastAt); iw.U32(id + 1); lastAt = off; }
            var iRaw = iw.ToArray();
            var iPacked = DataCompression.Compress(iRaw);
            var isec = new Writer(); isec.Name("il2wasm.inline");
            if (iPacked.Length + 8 < iRaw.Length) { isec.Byte(1); isec.U32(iRaw.Length); isec.Bytes(iPacked); }
            else { isec.Byte(0); isec.Bytes(iRaw); }
            w.Byte(0); w.U32(isec.Length); w.Bytes(isec.ToArray());
        }
        foreach (var p in _pdbProviders) p.Dispose();
        return w.ToArray();
    }
}

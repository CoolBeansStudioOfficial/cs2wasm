using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

/// <summary>Type names and signatures as text, the same whichever assembly a type is referenced through (a type's
/// full name, nested types after '/'), so a MemberRef's signature compares with a MethodDef's.</summary>
sealed class SigText(MetadataReader md) : ISignatureTypeProvider<string, object?>
{
    public string GetPrimitiveType(PrimitiveTypeCode t) => t switch
    {
        PrimitiveTypeCode.Boolean => "System.Boolean", PrimitiveTypeCode.Byte => "System.Byte", PrimitiveTypeCode.SByte => "System.SByte",
        PrimitiveTypeCode.Char => "System.Char", PrimitiveTypeCode.Int16 => "System.Int16", PrimitiveTypeCode.UInt16 => "System.UInt16",
        PrimitiveTypeCode.Int32 => "System.Int32", PrimitiveTypeCode.UInt32 => "System.UInt32", PrimitiveTypeCode.Int64 => "System.Int64",
        PrimitiveTypeCode.UInt64 => "System.UInt64", PrimitiveTypeCode.Single => "System.Single", PrimitiveTypeCode.Double => "System.Double",
        PrimitiveTypeCode.IntPtr => "System.IntPtr", PrimitiveTypeCode.UIntPtr => "System.UIntPtr", PrimitiveTypeCode.Object => "System.Object",
        PrimitiveTypeCode.String => "System.String", PrimitiveTypeCode.Void => "System.Void", PrimitiveTypeCode.TypedReference => "System.TypedReference",
        _ => t.ToString(),
    };
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => Meta.Name(r, h);
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => Meta.Name(r, h);
    public string GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) => r.GetTypeSpecification(h).DecodeSignature(this, c);
    public string GetSZArrayType(string e) => e + "[]";
    public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', s.Rank - 1) + "]";
    public string GetByReferenceType(string e) => e + "&";
    public string GetPointerType(string e) => e + "*";
    public string GetPinnedType(string e) => e + " pinned";
    public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
    public string GetGenericTypeParameter(object? c, int i) => "!" + i;
    public string GetGenericMethodParameter(object? c, int i) => "!!" + i;
    public string GetModifiedType(string m, string u, bool req) => u + (req ? " modreq(" : " modopt(") + m + ")";
    public string GetFunctionPointerType(MethodSignature<string> s) => "method " + Meta.Sig(s);
}

static class Meta
{
    public static string Name(MetadataReader r, TypeDefinitionHandle h)
    {
        var t = r.GetTypeDefinition(h);
        var n = r.GetString(t.Name);
        if (t.GetDeclaringType() is { IsNil: false } d) return Name(r, d) + "/" + n;
        var ns = r.GetString(t.Namespace);
        return ns.Length > 0 ? ns + "." + n : n;
    }

    public static string Name(MetadataReader r, TypeReferenceHandle h)
    {
        var t = r.GetTypeReference(h);
        var n = r.GetString(t.Name);
        if (t.ResolutionScope.Kind == HandleKind.TypeReference) return Name(r, (TypeReferenceHandle)t.ResolutionScope) + "/" + n;
        var ns = r.GetString(t.Namespace);
        return ns.Length > 0 ? ns + "." + n : n;
    }

    /// <summary>The assembly a TypeRef's outermost type is in (null: this module).</summary>
    public static string? Scope(MetadataReader r, TypeReferenceHandle h)
    {
        var t = r.GetTypeReference(h);
        return t.ResolutionScope.Kind switch
        {
            HandleKind.TypeReference => Scope(r, (TypeReferenceHandle)t.ResolutionScope),
            HandleKind.AssemblyReference => r.GetString(r.GetAssemblyReference((AssemblyReferenceHandle)t.ResolutionScope).Name),
            _ => null,
        };
    }

    public static string Sig(MethodSignature<string> s) =>
        (s.Header.IsInstance ? "instance " : "") + (s.GenericParameterCount > 0 ? "<" + s.GenericParameterCount + "> " : "") + s.ReturnType + "(" + string.Join(",", s.ParameterTypes) + ")";

    public static string MethodSig(MetadataReader r, BlobHandle b)
    {
        var br = r.GetBlobReader(b);
        return Sig(new SignatureDecoder<string, object?>(new SigText(r), r, null).DecodeMethodSignature(ref br));
    }

    public static string FieldSig(MetadataReader r, BlobHandle b)
    {
        var br = r.GetBlobReader(b);
        return new SignatureDecoder<string, object?>(new SigText(r), r, null).DecodeFieldSignature(ref br);
    }

    /// <summary>A MemberRef's parent type: (assembly, type name) for a TypeRef or a generic instantiation of one.</summary>
    public static (string? Asm, string Type)? Parent(MetadataReader r, EntityHandle parent)
    {
        switch (parent.Kind)
        {
            case HandleKind.TypeReference: return (Scope(r, (TypeReferenceHandle)parent), Name(r, (TypeReferenceHandle)parent));
            case HandleKind.TypeSpecification:
                {
                    var br = r.GetBlobReader(r.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
                    // GENERICINST (CLASS|VALUETYPE) TypeDefOrRef
                    if (br.ReadByte() != 0x15) return null;
                    br.ReadByte();
                    var h = br.ReadTypeHandle();
                    return h.Kind == HandleKind.TypeReference ? (Scope(r, (TypeReferenceHandle)h), Name(r, (TypeReferenceHandle)h)) : null;
                }
            default: return null;
        }
    }
}

/// <summary>An assembly's own types and members, by text.</summary>
sealed class Index
{
    public readonly Dictionary<string, TypeDefinitionHandle> Types = [];
    public readonly MetadataReader Md;
    readonly PEReader _pe;
    readonly Dictionary<string, HashSet<string>> _members = [];

    public Index(string path)
    {
        _pe = new PEReader(File.OpenRead(path));
        Md = _pe.GetMetadataReader();
        foreach (var h in Md.TypeDefinitions) Types[Meta.Name(Md, h)] = h;
    }

    public bool IsVisible(TypeDefinitionHandle h)
    {
        var t = Md.GetTypeDefinition(h);
        var v = t.Attributes & TypeAttributes.VisibilityMask;
        if (v == TypeAttributes.Public) return true;
        if (v is TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem) return IsVisible(t.GetDeclaringType());
        return false;
    }

    /// <summary>"M name sig" / "F name type" with whether each is visible outside the assembly.</summary>
    public HashSet<string> Members(string type)
    {
        if (_members.TryGetValue(type, out var s)) return s;
        s = [];
        if (Types.TryGetValue(type, out var h))
        {
            var t = Md.GetTypeDefinition(h);
            foreach (var mh in t.GetMethods())
            {
                var m = Md.GetMethodDefinition(mh);
                var acc = m.Attributes & MethodAttributes.MemberAccessMask;
                string key = "M " + Md.GetString(m.Name) + " " + Meta.MethodSig(Md, m.Signature);
                s.Add(key);
                if (acc is MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem) s.Add("+" + key);
            }
            foreach (var fh in t.GetFields())
            {
                var f = Md.GetFieldDefinition(fh);
                var acc = f.Attributes & FieldAttributes.FieldAccessMask;
                string key = "F " + Md.GetString(f.Name) + " " + Meta.FieldSig(Md, f.Signature);
                s.Add(key);
                if (acc is FieldAttributes.Public or FieldAttributes.Family or FieldAttributes.FamORAssem) s.Add("+" + key);
            }
        }
        return _members[type] = s;
    }

    /// <summary>The base types of a type, in this assembly (by name: a base from another assembly ends the chain).</summary>
    public IEnumerable<string> SelfAndBases(string type)
    {
        for (int guard = 0; guard < 64 && Types.TryGetValue(type, out var h); guard++)
        {
            yield return type;
            var b = Md.GetTypeDefinition(h).BaseType;
            if (b.IsNil) yield break;
            type = b.Kind switch
            {
                HandleKind.TypeDefinition => Meta.Name(Md, (TypeDefinitionHandle)b),
                HandleKind.TypeReference => Meta.Name(Md, (TypeReferenceHandle)b),
                HandleKind.TypeSpecification => Meta.Parent(Md, b)?.Type ?? "",
                _ => "",
            };
        }
    }
}

/// <summary>For every TypeRef and MemberRef that the stock libraries make into System.Private.CoreLib (directly, or
/// through a facade of the pack that forwards the type there), whether the given CoreLib has it, visible.</summary>
static class RefCheck
{
    /// <summary>(assembly, type) of the pack's facades forwarded to CoreLib.</summary>
    public static HashSet<(string, string)> ForwardedToCoreLib(string packLib)
    {
        var set = new HashSet<(string, string)>();
        foreach (var f in Directory.EnumerateFiles(packLib, "*.dll"))
        {
            using var pe = new PEReader(File.OpenRead(f));
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            var asm = md.GetString(md.GetAssemblyDefinition().Name);
            foreach (var eh in md.ExportedTypes)
            {
                var e = md.GetExportedType(eh);
                if (!e.IsForwarder || e.Implementation.Kind != HandleKind.AssemblyReference) continue;
                if (md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)e.Implementation).Name) != "System.Private.CoreLib") continue;
                var ns = md.GetString(e.Namespace);
                set.Add((asm, (ns.Length > 0 ? ns + "." : "") + md.GetString(e.Name)));
            }
        }
        return set;
    }

    public static bool IsCoreLib(HashSet<(string, string)> fwd, string? asm, string type)
    {
        if (asm == null) return false;
        if (asm == "System.Private.CoreLib") return true;
        int slash = type.IndexOf('/');
        return fwd.Contains((asm, slash < 0 ? type : type[..slash]));
    }

    public static int Run(string corelibPath, string packLib)
    {
        var core = new Index(corelibPath);
        var fwd = ForwardedToCoreLib(packLib);
        var missingTypes = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var hiddenTypes = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var missingMembers = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var hiddenMembers = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        int typeRefs = 0, memberRefs = 0, files = 0;
        void Add(SortedDictionary<string, SortedSet<string>> d, string k, string who) { if (!d.TryGetValue(k, out var s)) d[k] = s = new(StringComparer.Ordinal); s.Add(who); }

        foreach (var f in Directory.EnumerateFiles(packLib, "*.dll").Order(StringComparer.Ordinal))
        {
            using var pe = new PEReader(File.OpenRead(f));
            if (!pe.HasMetadata) continue;
            files++;
            var md = pe.GetMetadataReader();
            var who = Path.GetFileNameWithoutExtension(f);
            foreach (var th in md.TypeReferences)
            {
                var name = Meta.Name(md, th);
                if (!IsCoreLib(fwd, Meta.Scope(md, th), name)) continue;
                typeRefs++;
                if (!core.Types.TryGetValue(name, out var def)) Add(missingTypes, name, who);
                else if (!core.IsVisible(def)) Add(hiddenTypes, name, who);
            }
            foreach (var mh in md.MemberReferences)
            {
                var m = md.GetMemberReference(mh);
                if (Meta.Parent(md, m.Parent) is not { } p || !IsCoreLib(fwd, p.Asm, p.Type)) continue;
                memberRefs++;
                var name = md.GetString(m.Name);
                string key = m.GetKind() == MemberReferenceKind.Method
                    ? "M " + name + " " + Meta.MethodSig(md, m.Signature)
                    : "F " + name + " " + Meta.FieldSig(md, m.Signature);
                if (!core.Types.ContainsKey(p.Type)) continue; // (reported with the type)
                var chain = core.SelfAndBases(p.Type).ToList();
                if (chain.Any(t => core.Members(t).Contains("+" + key))) continue;
                if (chain.Any(t => core.Members(t).Contains(key))) Add(hiddenMembers, p.Type + " :: " + key[2..], who);
                else Add(missingMembers, p.Type + " :: " + key[2..], who);
            }
        }
        Console.WriteLine($"# {corelibPath}");
        Console.WriteLine($"# {files} assemblies of {packLib}: {typeRefs} TypeRefs and {memberRefs} MemberRefs into System.Private.CoreLib");
        Console.WriteLine($"# missing types {missingTypes.Count}, types not public {hiddenTypes.Count}, missing members {missingMembers.Count}, members not public {hiddenMembers.Count}");
        void Print(string title, SortedDictionary<string, SortedSet<string>> d)
        {
            Console.WriteLine();
            Console.WriteLine($"## {title} ({d.Count})");
            foreach (var (k, v) in d) Console.WriteLine($"{k}    [{string.Join(", ", v)}]");
        }
        Print("missing types", missingTypes);
        Print("types not public", hiddenTypes);
        Print("missing members", missingMembers);
        Print("members not public", hiddenMembers);
        return 0;
    }
}

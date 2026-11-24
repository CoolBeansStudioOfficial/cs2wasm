#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;
using System.Xml.Linq;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

namespace Il2Wasm.IL.Reach;

/// <summary>The feature switches a program is compiled with (SPEC 5: feature switches folded): the values .NET's
/// browser-wasm target gives them, then the project's own (its runtimeconfig.json's configProperties, which is where
/// RuntimeHostConfigurationOption items and MSBuild's feature properties such as EventSourceSupport land). A switch
/// with no value here is unknown: nothing that depends on it is folded.</summary>
public static class Features
{
    static readonly Dictionary<string, string> s_values = new(StringComparer.Ordinal)
    {
        // (the browser-wasm SDK's defaults: EventSourceSupport=false; the IL front end's CoreLib is invariant, as
        // Constants.Known folds GlobalizationMode.Invariant; resources are kept: UseSystemResourceKeys=false)
        ["System.Diagnostics.Tracing.EventSource.IsSupported"] = "false",
        ["System.Globalization.Invariant"] = "true",
        ["System.Resources.UseSystemResourceKeys"] = "false",
    };

    public static string? Value(string name) => s_values.GetValueOrDefault(name);

    public static bool? Bool(string name) => Value(name) is { } v ? string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) : null;

    /// <summary>A project's runtimeconfig.json: its configProperties override the defaults.</summary>
    public static void LoadRuntimeConfig(string path)
    {
        if (!File.Exists(path)) return;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("runtimeOptions", out var ro) || !ro.TryGetProperty("configProperties", out var cp)) return;
        foreach (var p in cp.EnumerateObject())
            s_values[p.Name] = p.Value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.True => "true", System.Text.Json.JsonValueKind.False => "false",
                _ => p.Value.ToString(),
            };
    }

    public static void Set(string name, string value) => s_values[name] = value;
}

/// <summary>ILLink's substitutions (the ILLink.Substitutions.xml resource each assembly may carry, CoreLib's included:
/// tools/BuildCoreLib embeds the runtime's), applied as the trimmer applies them: a method's body replaced by a stub
/// that returns a constant (or nothing), a static field's value fixed, each one only under the feature switch values it
/// names (Features). The compiler compiles the stub as the method's body, and the constant propagation (Sccp.cs) knows
/// the value at each call, so code behind a switch that is off is never reached (IL2WASM_NO_SUBSTITUTIONS=1: none).</summary>
public sealed class Substitutions
{
    public sealed record Stub(bool HasValue, long Value);

    readonly Dictionary<MethodDesc, Stub> _methods = new();
    readonly Dictionary<FieldDesc, long> _fields = new();
    readonly HashSet<ModuleDesc> _loaded = [];
    public static readonly bool Off = Environment.GetEnvironmentVariable("IL2WASM_NO_SUBSTITUTIONS") == "1";

    public Stub? Method(MethodDesc m)
    {
        if (Off || m.GetTypicalMethodDefinition() is not EcmaMethod em) return null;
        Load(em.Module);
        return _methods.GetValueOrDefault(em);
    }

    public long? Field(FieldDesc f)
    {
        if (Off || f.GetTypicalFieldDefinition() is not EcmaField ef) return null;
        Load(ef.Module);
        return _fields.TryGetValue(ef, out var v) ? v : null;
    }

    void Load(EcmaModule module)
    {
        if (!_loaded.Add(module)) return;
        var md = module.MetadataReader;
        foreach (var h in md.ManifestResources)
        {
            var r = md.GetManifestResource(h);
            if (!r.Implementation.IsNil || md.GetString(r.Name) != "ILLink.Substitutions.xml") continue;
            var dir = module.PEReader.PEHeaders.CorHeader!.ResourcesDirectory;
            if (!module.PEReader.PEHeaders.TryGetDirectoryOffset(dir, out _)) continue;
            var block = module.PEReader.GetSectionData(dir.RelativeVirtualAddress);
            var reader = block.GetReader((int)r.Offset, block.Length - (int)r.Offset);
            int len = reader.ReadInt32();
            using var xml = new MemoryStream(reader.ReadBytes(len));
            Apply(module, XDocument.Load(xml));
        }
    }

    static bool Enabled(XElement e)
    {
        var feature = (string?)e.Attribute("feature");
        if (feature == null) return true;
        var want = (string?)e.Attribute("featurevalue") ?? "true";
        var have = Features.Value(feature);
        if (have == null) return string.Equals((string?)e.Attribute("featuredefault"), "true", StringComparison.OrdinalIgnoreCase);
        return string.Equals(have, want, StringComparison.OrdinalIgnoreCase);
    }

    void Apply(EcmaModule module, XDocument doc)
    {
        var name = module.Assembly.GetName().Name;
        foreach (var asm in doc.Root!.Elements("assembly"))
        {
            if ((string?)asm.Attribute("fullname") is { } fn && fn != name) continue;
            if (!Enabled(asm)) continue;
            foreach (var t in asm.Elements("type")) ApplyType(module, t, null);
        }
    }

    void ApplyType(EcmaModule module, XElement te, MetadataType? outer)
    {
        if (!Enabled(te)) return;
        var full = (string?)te.Attribute("fullname");
        if (full == null) return;
        MetadataType? type = outer;
        if (outer == null)
        {
            var parts = full.Split('/');
            int dot = parts[0].LastIndexOf('.');
            type = module.GetType(Encoding.UTF8.GetBytes(dot < 0 ? "" : parts[0][..dot]), Encoding.UTF8.GetBytes(dot < 0 ? parts[0] : parts[0][(dot + 1)..]), throwIfNotFound: false) as MetadataType;
            for (int i = 1; i < parts.Length && type != null; i++) type = type.GetNestedType(Encoding.UTF8.GetBytes(parts[i]));
        }
        else type = outer.GetNestedType(Encoding.UTF8.GetBytes(full));
        if (type == null) return;
        foreach (var me in te.Elements("method"))
        {
            if (!Enabled(me) || (string?)me.Attribute("body") != "stub") continue;
            var sig = (string?)me.Attribute("signature");
            var m = type.GetMethods().FirstOrDefault(x => Signature(x) == sig);
            if (m == null) continue;
            var value = (string?)me.Attribute("value");
            var rt = m.Signature.ReturnType;
            if (rt.IsVoid) { _methods[m] = new Stub(false, 0); continue; }
            if (value == null) value = "0";
            if (Parse(value) is long v && rt.IsPrimitive) _methods[m] = new Stub(true, v);
        }
        foreach (var fe in te.Elements("field"))
        {
            if (!Enabled(fe)) continue;
            var f = type.GetField(Encoding.UTF8.GetBytes((string?)fe.Attribute("name") ?? ""));
            if (f == null || !f.IsStatic || !f.FieldType.IsPrimitive) continue;
            if (Parse((string?)fe.Attribute("value") ?? "") is long v) _fields[f] = v;
        }
        foreach (var nt in te.Elements("type")) ApplyType(module, nt, type);
    }

    static long? Parse(string s) => s.ToLowerInvariant() switch
    {
        "true" => 1, "false" => 0,
        _ => long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null,
    };

    /// <summary>ILLink's method signature text: "ReturnType Name(Param1,Param2)", types by full name.</summary>
    public static string Signature(MethodDesc m)
    {
        var sb = new StringBuilder();
        sb.Append(TypeName(m.Signature.ReturnType)).Append(' ').Append(m.GetName()).Append('(');
        for (int i = 0; i < m.Signature.Length; i++) { if (i > 0) sb.Append(','); sb.Append(TypeName(m.Signature[i])); }
        return sb.Append(')').ToString();
    }

    static string TypeName(TypeDesc t) => t switch
    {
        ArrayType a => TypeName(a.ElementType) + (a.IsSzArray ? "[]" : "[" + new string(',', a.Rank - 1) + "]"),
        ByRefType r => TypeName(r.ParameterType) + "&",
        PointerType p => TypeName(p.ParameterType) + "*",
        MetadataType mt => (mt.ContainingType is MetadataType ct ? TypeName(ct) + "/" : mt.GetNamespace().Length > 0 ? mt.GetNamespace() + "." : "") + mt.GetName()
            + (mt.HasInstantiation && !mt.IsTypeDefinition ? "<" + string.Join(",", Enumerable.Range(0, mt.Instantiation.Length).Select(i => TypeName(mt.Instantiation[i]))) + ">" : ""),
        _ => t.ToString()!,
    };
}

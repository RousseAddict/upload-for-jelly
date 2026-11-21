// Answers, for a plugin dll that is already built, the question a compiler cannot: does
// every type and member it references exist, with the same signature, in the Jellyfin it
// is about to be loaded into? A parameter that gains a default value recompiles cleanly
// and still throws MissingMethodException from the old binary; this finds that without
// starting a server. docs/03-jellyfin-12.md §4 is the record of its first use.
//
// Each reference is resolved the way the runtime binds it: by assembly name, following
// type forwarders, walking base types and interfaces, then matching name and decoded
// signature. Only the BCL's own System.Reflection.Metadata is used - no package.
//
// With --contracts it also prints, for every host interface or base class the plugin
// implements or derives from, the abstract and default-interface members the host
// declares. Diff that output between two hosts: a member added there is a
// TypeLoadException here.
//
// The host directory must hold what the server actually loads, framework included. The
// official image ships it all in /jellyfin, so on a machine with podman:
//
//     c=$(podman create docker.io/jellyfin/jellyfin@sha256:<digest>)
//     mkdir host && podman cp "$c:/jellyfin" - | tar -x -C host --strip-components=1
//     podman rm "$c"
//
// Then, wherever the .NET SDK is:
//
//     dotnet run --project tools/abicheck -c Release -- plugin.dll host [--contracts]
//
// Exit code 0 when everything resolves, 1 otherwise. Run it against the previous host
// too, and against one known to be incompatible: zero unresolved means something only
// if the same run reports the break it should.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: abicheck <plugin.dll> <host-assembly-dir> [--contracts]");
    return 2;
}

var pluginPath = args[0];
var hostDir = args[1];
var host = new Host(hostDir);
var plugin = new Asm(pluginPath);
var md = plugin.Reader;

int typeRefs = 0, memberRefs = 0, bad = 0;

foreach (var h in md.TypeReferences)
{
    typeRefs++;
    var r = host.ResolveTypeRef(plugin, h);
    if (r is null)
    {
        bad++;
        Console.WriteLine($"UNRESOLVED TYPE   {plugin.TypeRefName(h)} (scope {plugin.ScopeName(h)})");
    }
}

foreach (var h in md.MemberReferences)
{
    memberRefs++;
    var mr = md.GetMemberReference(h);
    var name = md.GetString(mr.Name);
    var parent = host.ResolveParent(plugin, mr.Parent);
    if (parent is null)
    {
        // A MethodDefinition or ModuleReference parent (vararg, P/Invoke) is local; the
        // plugin has none, so anything here is an unresolved type already reported.
        if (mr.Parent.Kind is HandleKind.TypeReference or HandleKind.TypeSpecification)
        {
            bad++;
            Console.WriteLine($"UNRESOLVED PARENT {name}");
        }

        continue;
    }

    var wanted = plugin.MemberRefSig(mr);
    if (!host.HasMember(parent.Value, name, wanted, mr.GetKind()))
    {
        bad++;
        Console.WriteLine($"UNRESOLVED MEMBER {parent.Value.Asm.FullTypeName(parent.Value.Type)}::{name} {wanted}");
        foreach (var c in host.Candidates(parent.Value, name))
        {
            Console.WriteLine($"    host has: {c}");
        }
    }
}

Console.WriteLine($"{typeRefs} type references, {memberRefs} member references, {bad} unresolved");

if (args.Length > 2 && args[2] == "--contracts")
{
    // Every external type the plugin's own types derive from or implement, and what the
    // host requires of an implementer.
    var seen = new HashSet<string>();
    foreach (var th in md.TypeDefinitions)
    {
        var td = md.GetTypeDefinition(th);
        var supers = new List<EntityHandle>();
        if (!td.BaseType.IsNil)
        {
            supers.Add(td.BaseType);
        }

        supers.AddRange(td.GetInterfaceImplementations().Select(i => md.GetInterfaceImplementation(i).Interface));
        foreach (var s in supers)
        {
            var r = host.ResolveParent(plugin, s);
            if (r is null)
            {
                continue;
            }

            foreach (var t in host.SelfAndBases(r.Value).Concat(host.AllInterfaces(r.Value)))
            {
                var key = t.Asm.FullTypeName(t.Type);
                if (!seen.Add(key))
                {
                    continue;
                }

                foreach (var line in host.AbstractMembers(t).OrderBy(x => x, StringComparer.Ordinal))
                {
                    Console.WriteLine($"CONTRACT {key} :: {line}");
                }
            }
        }
    }
}

return bad == 0 ? 0 : 1;

internal readonly record struct TypeIn(Asm Asm, TypeDefinitionHandle Type);

internal sealed class Asm
{
    public Asm(string path)
    {
        Path = path;
        Pe = new PEReader(File.OpenRead(path));
        Reader = Pe.GetMetadataReader();
        Name = Reader.GetString(Reader.GetAssemblyDefinition().Name);
        foreach (var h in Reader.TypeDefinitions)
        {
            Types[FullTypeName(h)] = h;
        }

        foreach (var h in Reader.ExportedTypes)
        {
            var e = Reader.GetExportedType(h);
            Exports[ExportedName(e)] = e;
        }
    }

    public string Path { get; }

    public PEReader Pe { get; }

    public MetadataReader Reader { get; }

    public string Name { get; }

    public Dictionary<string, TypeDefinitionHandle> Types { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, ExportedType> Exports { get; } = new(StringComparer.Ordinal);

    public string FullTypeName(TypeDefinitionHandle h)
    {
        var t = Reader.GetTypeDefinition(h);
        var name = Reader.GetString(t.Name);
        if (t.IsNested)
        {
            return FullTypeName(t.GetDeclaringType()) + "/" + name;
        }

        var ns = Reader.GetString(t.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public string TypeRefName(TypeReferenceHandle h)
    {
        var t = Reader.GetTypeReference(h);
        var name = Reader.GetString(t.Name);
        if (t.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return TypeRefName((TypeReferenceHandle)t.ResolutionScope) + "/" + name;
        }

        var ns = Reader.GetString(t.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public string ScopeName(TypeReferenceHandle h)
    {
        var t = Reader.GetTypeReference(h);
        return t.ResolutionScope.Kind switch
        {
            HandleKind.AssemblyReference => Reader.GetString(Reader.GetAssemblyReference((AssemblyReferenceHandle)t.ResolutionScope).Name),
            HandleKind.TypeReference => ScopeName((TypeReferenceHandle)t.ResolutionScope),
            _ => "<local>",
        };
    }

    public string MemberRefSig(MemberReference mr)
    {
        var p = new SigProvider(this);
        return mr.GetKind() == MemberReferenceKind.Method
            ? Render(mr.DecodeMethodSignature(p, null))
            : mr.DecodeFieldSignature(p, null);
    }

    public string MethodSig(MethodDefinition m)
    {
        return Render(m.DecodeSignature(new SigProvider(this), null));
    }

    public string FieldSig(FieldDefinition f)
    {
        return f.DecodeSignature(new SigProvider(this), null);
    }

    private static string Render(MethodSignature<string> s)
    {
        var g = s.GenericParameterCount > 0 ? $"`{s.GenericParameterCount}" : string.Empty;
        var inst = s.Header.IsInstance ? "instance " : string.Empty;
        return $"{inst}{s.ReturnType}{g}({string.Join(",", s.ParameterTypes)})";
    }

    private string ExportedName(ExportedType e)
    {
        var name = Reader.GetString(e.Name);
        if (e.Implementation.Kind == HandleKind.ExportedType)
        {
            return ExportedName(Reader.GetExportedType((ExportedTypeHandle)e.Implementation)) + "/" + name;
        }

        var ns = Reader.GetString(e.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }
}

// Renders a type the way identity is decided at bind time minus the assembly, which
// forwarding legitimately changes: namespace-qualified name, generic arity, arguments.
internal sealed class SigProvider : ISignatureTypeProvider<string, object?>
{
    private readonly Asm _asm;

    public SigProvider(Asm asm) => _asm = asm;

    public string GetArrayType(string e, ArrayShape s) => $"{e}[{new string(',', s.Rank - 1)}]";

    public string GetByReferenceType(string e) => e + "&";

    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";

    public string GetGenericInstantiation(string g, ImmutableArray<string> a) => $"{g}<{string.Join(",", a)}>";

    public string GetGenericMethodParameter(object? c, int i) => "!!" + i;

    public string GetGenericTypeParameter(object? c, int i) => "!" + i;

    public string GetModifiedType(string m, string u, bool r) => u;

    public string GetPinnedType(string e) => e + " pinned";

    public string GetPointerType(string e) => e + "*";

    public string GetPrimitiveType(PrimitiveTypeCode t) => t.ToString();

    public string GetSZArrayType(string e) => e + "[]";

    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => _asm.FullTypeName(h);

    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => _asm.TypeRefName(h);

    public string GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k)
        => r.GetTypeSpecification(h).DecodeSignature(this, c);
}

internal sealed class Host
{
    private readonly Dictionary<string, Asm?> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _dir;

    public Host(string dir) => _dir = dir;

    public Asm? Load(string name)
    {
        if (_byName.TryGetValue(name, out var a))
        {
            return a;
        }

        var path = System.IO.Path.Combine(_dir, name + ".dll");
        a = File.Exists(path) ? new Asm(path) : null;
        _byName[name] = a;
        return a;
    }

    public TypeIn? Find(string asmName, string fullName, int depth = 0)
    {
        var a = Load(asmName);
        if (a is null || depth > 8)
        {
            return null;
        }

        if (a.Types.TryGetValue(fullName, out var h))
        {
            return new TypeIn(a, h);
        }

        // A nested type travels with its outermost type: a forwarder only names the outer
        // one, so resolve that and look for the nested name where it landed.
        var slash = fullName.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0)
        {
            var outer = Find(asmName, fullName[..slash], depth + 1);
            if (outer is not null && !ReferenceEquals(outer.Value.Asm, a) && outer.Value.Asm.Types.TryGetValue(fullName, out var nested))
            {
                return new TypeIn(outer.Value.Asm, nested);
            }
        }

        if (a.Exports.TryGetValue(fullName, out var e) && e.Implementation.Kind == HandleKind.AssemblyReference)
        {
            var target = a.Reader.GetString(a.Reader.GetAssemblyReference((AssemblyReferenceHandle)e.Implementation).Name);
            return Find(target, fullName, depth + 1);
        }

        return null;
    }

    public TypeIn? ResolveTypeRef(Asm from, TypeReferenceHandle h)
    {
        return Find(from.ScopeName(h), from.TypeRefName(h));
    }

    public TypeIn? ResolveParent(Asm from, EntityHandle parent)
    {
        switch (parent.Kind)
        {
            case HandleKind.TypeReference:
                return ResolveTypeRef(from, (TypeReferenceHandle)parent);
            case HandleKind.TypeDefinition:
                return new TypeIn(from, (TypeDefinitionHandle)parent);
            case HandleKind.TypeSpecification:
                var blob = from.Reader.GetBlobReader(from.Reader.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
                var code = blob.ReadSignatureTypeCode();
                if (code == SignatureTypeCode.GenericTypeInstance)
                {
                    blob.ReadSignatureTypeCode();
                    var generic = blob.ReadTypeHandle();
                    return ResolveParent(from, generic);
                }

                return null;
            default:
                return null;
        }
    }

    public IEnumerable<TypeIn> SelfAndBases(TypeIn t)
    {
        TypeIn? cur = t;
        var guard = 0;
        while (cur is not null && guard++ < 32)
        {
            yield return cur.Value;
            var td = cur.Value.Asm.Reader.GetTypeDefinition(cur.Value.Type);
            cur = td.BaseType.IsNil ? null : ResolveParent(cur.Value.Asm, td.BaseType);
        }
    }

    public IEnumerable<TypeIn> AllInterfaces(TypeIn t)
    {
        var seen = new HashSet<string>();
        var queue = new Queue<TypeIn>(SelfAndBases(t));
        while (queue.Count > 0)
        {
            var x = queue.Dequeue();
            var td = x.Asm.Reader.GetTypeDefinition(x.Type);
            foreach (var ih in td.GetInterfaceImplementations())
            {
                var i = ResolveParent(x.Asm, x.Asm.Reader.GetInterfaceImplementation(ih).Interface);
                if (i is not null && seen.Add(i.Value.Asm.FullTypeName(i.Value.Type)))
                {
                    yield return i.Value;
                    queue.Enqueue(i.Value);
                }
            }
        }
    }

    public bool HasMember(TypeIn t, string name, string sig, MemberReferenceKind kind)
    {
        // Instance and static methods are looked up the same way; interfaces have no
        // base type, so a call through one is checked on the interface and its parents.
        var search = SelfAndBases(t).Concat(AllInterfaces(t));
        foreach (var x in search)
        {
            var r = x.Asm.Reader;
            var td = r.GetTypeDefinition(x.Type);
            if (kind == MemberReferenceKind.Method)
            {
                foreach (var mh in td.GetMethods())
                {
                    var m = r.GetMethodDefinition(mh);
                    if (r.GetString(m.Name) == name && x.Asm.MethodSig(m) == sig)
                    {
                        return true;
                    }
                }
            }
            else
            {
                foreach (var fh in td.GetFields())
                {
                    var f = r.GetFieldDefinition(fh);
                    if (r.GetString(f.Name) == name && x.Asm.FieldSig(f) == sig)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    public IEnumerable<string> Candidates(TypeIn t, string name)
    {
        foreach (var x in SelfAndBases(t).Concat(AllInterfaces(t)))
        {
            var r = x.Asm.Reader;
            var td = r.GetTypeDefinition(x.Type);
            foreach (var mh in td.GetMethods())
            {
                var m = r.GetMethodDefinition(mh);
                if (r.GetString(m.Name) == name)
                {
                    yield return $"{x.Asm.FullTypeName(x.Type)}::{name} {x.Asm.MethodSig(m)}";
                }
            }
        }
    }

    public IEnumerable<string> AbstractMembers(TypeIn t)
    {
        var r = t.Asm.Reader;
        var td = r.GetTypeDefinition(t.Type);
        foreach (var mh in td.GetMethods())
        {
            var m = r.GetMethodDefinition(mh);
            if ((m.Attributes & MethodAttributes.Abstract) != 0)
            {
                yield return $"abstract {r.GetString(m.Name)} {t.Asm.MethodSig(m)}";
            }
            else if ((m.Attributes & MethodAttributes.Virtual) != 0 && (td.Attributes & TypeAttributes.Interface) != 0)
            {
                yield return $"default-impl {r.GetString(m.Name)} {t.Asm.MethodSig(m)}";
            }
        }
    }
}

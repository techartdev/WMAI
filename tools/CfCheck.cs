// CfCheck: lists framework types/members an assembly references that do not
// exist in the device's real NETCF 3.5 assemblies (tools/cfref, pulled from the
// phone). Compares full signatures, so overloads like Interlocked.Increment(long)
// are caught. Loaded by cfcheck.ps1 via Add-Type (needs PowerShell 7).
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

public sealed class CfCheck
{
    // Signature -> readable text, the same way for both sides.
    sealed class Names : ISignatureTypeProvider<string, object>
    {
        readonly MetadataReader md;
        public Names(MetadataReader md) { this.md = md; }
        public string GetPrimitiveType(PrimitiveTypeCode t) { return t.ToString(); }
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) { return FullName(r, h); }
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) { return FullName(r, h); }
        public string GetTypeFromSpecification(MetadataReader r, object c, TypeSpecificationHandle h, byte k)
        { return r.GetTypeSpecification(h).DecodeSignature(this, c); }
        public string GetSZArrayType(string e) { return e + "[]"; }
        public string GetArrayType(string e, ArrayShape s) { return e + "[" + new string(',', s.Rank - 1) + "]"; }
        public string GetByReferenceType(string e) { return e + "&"; }
        public string GetPointerType(string e) { return e + "*"; }
        public string GetPinnedType(string e) { return e; }
        public string GetModifiedType(string m, string u, bool req) { return u; }
        public string GetGenericInstantiation(string g, ImmutableArray<string> a) { return g + "<" + string.Join(",", a) + ">"; }
        public string GetGenericMethodParameter(object c, int i) { return "!!" + i; }
        public string GetGenericTypeParameter(object c, int i) { return "!" + i; }
        public string GetFunctionPointerType(MethodSignature<string> s) { return "fnptr"; }
    }

    static string FullName(MetadataReader r, TypeDefinitionHandle h)
    {
        TypeDefinition t = r.GetTypeDefinition(h);
        string name = r.GetString(t.Name);
        if (t.GetDeclaringType().IsNil == false) return FullName(r, t.GetDeclaringType()) + "/" + name;
        string ns = r.GetString(t.Namespace);
        return ns.Length > 0 ? ns + "." + name : name;
    }

    static string FullName(MetadataReader r, TypeReferenceHandle h)
    {
        TypeReference t = r.GetTypeReference(h);
        string name = r.GetString(t.Name);
        if (t.ResolutionScope.Kind == HandleKind.TypeReference)
            return FullName(r, (TypeReferenceHandle)t.ResolutionScope) + "/" + name;
        string ns = r.GetString(t.Namespace);
        return ns.Length > 0 ? ns + "." + name : name;
    }

    static string MethodSig(MethodSignature<string> s)
    {
        string gen = s.GenericParameterCount > 0 ? "`" + s.GenericParameterCount : "";
        return gen + "(" + string.Join(",", s.ParameterTypes) + ")" + s.ReturnType;
    }

    class TypeInfo { public string Base; public HashSet<string> Members = new HashSet<string>(); }
    readonly Dictionary<string, TypeInfo> cf = new Dictionary<string, TypeInfo>();
    readonly List<PEReader> keep = new List<PEReader>();

    public CfCheck(string[] cfAssemblies)
    {
        foreach (string path in cfAssemblies)
        {
            PEReader pe = new PEReader(File.OpenRead(path));
            keep.Add(pe);
            MetadataReader md = pe.GetMetadataReader();
            Names names = new Names(md);
            foreach (TypeDefinitionHandle th in md.TypeDefinitions)
            {
                TypeDefinition td = md.GetTypeDefinition(th);
                TypeInfo ti = new TypeInfo();
                if (!td.BaseType.IsNil)
                {
                    if (td.BaseType.Kind == HandleKind.TypeDefinition) ti.Base = FullName(md, (TypeDefinitionHandle)td.BaseType);
                    else if (td.BaseType.Kind == HandleKind.TypeReference) ti.Base = FullName(md, (TypeReferenceHandle)td.BaseType);
                    else ti.Base = md.GetTypeSpecification((TypeSpecificationHandle)td.BaseType).DecodeSignature(names, null).Split('<')[0];
                }
                foreach (MethodDefinitionHandle mh in td.GetMethods())
                {
                    MethodDefinition m = md.GetMethodDefinition(mh);
                    ti.Members.Add(md.GetString(m.Name) + MethodSig(m.DecodeSignature(names, null)));
                }
                foreach (FieldDefinitionHandle fh in td.GetFields())
                {
                    FieldDefinition f = md.GetFieldDefinition(fh);
                    ti.Members.Add(md.GetString(f.Name) + ":" + f.DecodeSignature(names, null));
                }
                cf[FullName(md, th)] = ti;
            }
        }
    }

    bool HasMember(string type, string member)
    {
        for (int depth = 0; type != null && depth < 20; depth++)
        {
            TypeInfo ti;
            if (!cf.TryGetValue(type, out ti)) return false;
            if (ti.Members.Contains(member)) return true;
            type = ti.Base;
        }
        return false;
    }

    // Returns "missing" lines for the given assembly.
    public string[] Check(string path)
    {
        HashSet<string> missing = new HashSet<string>();
        using (PEReader pe = new PEReader(File.OpenRead(path)))
        {
            MetadataReader md = pe.GetMetadataReader();
            Names names = new Names(md);
            HashSet<string> framework = new HashSet<string> { "mscorlib", "System", "System.Windows.Forms", "System.Drawing" };

            // Custom attributes are only materialised on reflection, so attribute
            // constructors (and types used only as attributes) missing on the
            // device are harmless, e.g. the compiler's RuntimeCompatibilityAttribute.
            HashSet<EntityHandle> attrCtors = new HashSet<EntityHandle>();
            foreach (CustomAttributeHandle ch in md.CustomAttributes)
                attrCtors.Add(md.GetCustomAttribute(ch).Constructor);
            HashSet<EntityHandle> usedOutsideAttrs = new HashSet<EntityHandle>();
            foreach (MemberReferenceHandle mh in md.MemberReferences)
                if (!attrCtors.Contains(mh)) usedOutsideAttrs.Add(md.GetMemberReference(mh).Parent);

            foreach (TypeReferenceHandle th in md.TypeReferences)
            {
                TypeReference t = md.GetTypeReference(th);
                if (!FromFramework(md, t, framework)) continue;
                string name = FullName(md, th);
                bool attributeOnly = name.EndsWith("Attribute") && !usedOutsideAttrs.Contains(th);
                if (!cf.ContainsKey(name) && !attributeOnly) missing.Add("type   " + name);
            }

            foreach (MemberReferenceHandle mh in md.MemberReferences)
            {
                if (attrCtors.Contains(mh)) continue;
                MemberReference m = md.GetMemberReference(mh);
                string type;
                if (m.Parent.Kind == HandleKind.TypeReference)
                {
                    TypeReference t = md.GetTypeReference((TypeReferenceHandle)m.Parent);
                    if (!FromFramework(md, t, framework)) continue;
                    type = FullName(md, (TypeReferenceHandle)m.Parent);
                }
                else if (m.Parent.Kind == HandleKind.TypeSpecification)
                {
                    string spec = md.GetTypeSpecification((TypeSpecificationHandle)m.Parent).DecodeSignature(names, null);
                    type = spec.Split('<')[0];
                    if (!cf.ContainsKey(type) && !IsFrameworkName(md, (TypeSpecificationHandle)m.Parent, framework)) continue;
                }
                else continue;

                string member = md.GetString(m.Name);
                if (m.GetKind() == MemberReferenceKind.Method) member += MethodSig(m.DecodeMethodSignature(names, null));
                else member += ":" + m.DecodeFieldSignature(names, null);
                if (!HasMember(type, member)) missing.Add("member " + type + "::" + member);
            }
        }
        string[] result = missing.ToArray();
        Array.Sort(result, StringComparer.Ordinal);
        return result;
    }

    static bool FromFramework(MetadataReader md, TypeReference t, HashSet<string> framework)
    {
        while (t.ResolutionScope.Kind == HandleKind.TypeReference) t = md.GetTypeReference((TypeReferenceHandle)t.ResolutionScope);
        if (t.ResolutionScope.Kind != HandleKind.AssemblyReference) return false;
        return framework.Contains(md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)t.ResolutionScope).Name));
    }

    static bool IsFrameworkName(MetadataReader md, TypeSpecificationHandle h, HashSet<string> framework)
    {
        // Generic instantiation of a framework type: first TypeRef inside the blob decides.
        BlobReader b = md.GetBlobReader(md.GetTypeSpecification(h).Signature);
        if (b.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return false;
        b.ReadSignatureTypeCode();
        EntityHandle eh = b.ReadTypeHandle();
        return eh.Kind == HandleKind.TypeReference && FromFramework(md, md.GetTypeReference((TypeReferenceHandle)eh), framework);
    }
}

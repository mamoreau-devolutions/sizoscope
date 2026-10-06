using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Sizoscope.Tests.Fixtures;

// Encodes caller-supplied records only: aggregate and identity expectations live in tests.
internal sealed class MstatPeBuilder
{
    private readonly MetadataBuilder _metadata = new();
    private readonly BlobBuilder _names = new();
    private readonly Dictionary<string, BlobBuilder> _payloads = new()
    {
        ["Types"] = new(), ["Methods"] = new(), ["Blobs"] = new(),
        ["RvaFields"] = new(), ["FrozenObjects"] = new(), ["ManifestResources"] = new()
    };
    public Version Version { get; }
    public AssemblyReferenceHandle Assembly { get; }

    public MstatPeBuilder(string version = "2.1.0.0", bool padding = false)
    {
        Version = System.Version.Parse(version);
        _metadata.AddModule(0, _metadata.GetOrAddString("fixture.mstat"),
            _metadata.GetOrAddGuid(new Guid("026ab527-39e7-419b-939c-d8f851663df4")), default, default);
        _metadata.AddAssembly(_metadata.GetOrAddString("MstatFixture"), Version, default, default, 0,
            AssemblyHashAlgorithm.None);
        if (padding)
        {
            var padAssembly = AddAssembly("Padding");
            var padType = AddType("Unused", "Pad", padAssembly);
            AddMethodSpec(AddMethod("Unused", padType, generic: true), 0x08);
            AddTypeSpec(padType, 0x08);
        }
        Assembly = AddAssembly("Fixture.Library");
    }

    public AssemblyReferenceHandle AddAssembly(string name) =>
        _metadata.AddAssemblyReference(_metadata.GetOrAddString(name), new Version(1, 0, 0, 0),
            default, default, 0, default);

    public TypeReferenceHandle AddType(string name, string ns = "Example", EntityHandle scope = default) =>
        _metadata.AddTypeReference(scope.IsNil ? Assembly : scope, _metadata.GetOrAddString(ns),
            _metadata.GetOrAddString(name));

    public MemberReferenceHandle AddMethod(string name, EntityHandle owner, byte returnType = 0x01,
        byte[] parameters = null, bool generic = false)
    {
        parameters ??= Array.Empty<byte>();
        var signature = new BlobBuilder();
        signature.WriteByte(generic ? (byte)0x10 : (byte)0);
        if (generic) signature.WriteCompressedInteger(1);
        signature.WriteCompressedInteger(parameters.Length);
        signature.WriteByte(returnType);
        signature.WriteBytes(parameters);
        return _metadata.AddMemberReference(owner, _metadata.GetOrAddString(name), _metadata.GetOrAddBlob(signature));
    }

    public MemberReferenceHandle AddField(string name, EntityHandle owner, byte fieldType = 0x08) =>
        _metadata.AddMemberReference(owner, _metadata.GetOrAddString(name),
            _metadata.GetOrAddBlob(new byte[] { 0x06, fieldType }));

    public TypeSpecificationHandle AddTypeSpec(TypeReferenceHandle definition, byte argument)
    {
        var signature = new BlobBuilder();
        signature.WriteByte(0x15); // GENERICINST
        signature.WriteByte(0x12); // CLASS, followed by TypeDefOrRef coded index
        signature.WriteCompressedInteger((MetadataTokens.GetRowNumber(definition) << 2) | 1);
        signature.WriteCompressedInteger(1);
        signature.WriteByte(argument);
        return _metadata.AddTypeSpecification(_metadata.GetOrAddBlob(signature));
    }

    public MethodSpecificationHandle AddMethodSpec(MemberReferenceHandle definition, byte argument) =>
        _metadata.AddMethodSpecification(definition, _metadata.GetOrAddBlob(new byte[] { 0x0a, 1, argument }));

    public int AddName(string name)
    {
        int offset = _names.Count;
        _names.WriteSerializedString(name);
        return offset;
    }

    private static void Token(BlobBuilder payload, EntityHandle handle)
    {
        payload.WriteByte(0xd0); // ldtoken
        payload.WriteInt32(MetadataTokens.GetToken(handle));
    }

    private static void Constant(BlobBuilder payload, int value)
    {
        payload.WriteByte(0x20); // ldc.i4
        payload.WriteInt32(value);
    }

    private void String(BlobBuilder payload, string value)
    {
        payload.WriteByte(0x72); // ldstr
        payload.WriteInt32(MetadataTokens.GetToken(_metadata.GetOrAddUserString(value)));
    }

    public void Type(EntityHandle handle, int size, int nodeId = 0)
    {
        var p = _payloads["Types"];
        Token(p, handle); Constant(p, size);
        if (Version.Major >= 2) Constant(p, nodeId);
    }

    public void Method(EntityHandle handle, int code, int gc = 0, int unwind = 0, int nodeId = 0)
    {
        var p = _payloads["Methods"];
        Token(p, handle); Constant(p, code); Constant(p, gc); Constant(p, unwind);
        if (Version.Major >= 2) Constant(p, nodeId);
    }

    public void Field(MemberReferenceHandle handle, int size, int nodeId = 0)
    {
        var p = _payloads["RvaFields"];
        Token(p, handle); Constant(p, size); Constant(p, nodeId);
    }

    public void Frozen(EntityHandle instanceType, int size, int nodeId, EntityHandle owner = default)
    {
        var p = _payloads["FrozenObjects"];
        Token(p, instanceType); Constant(p, size); Constant(p, nodeId);
        if (owner.IsNil) p.WriteByte(0); // nop: explicit absent-owner sentinel
        else Token(p, owner);
    }

    public void Resource(string name, int size, AssemblyReferenceHandle assembly = default)
    {
        var p = _payloads["ManifestResources"];
        Constant(p, MetadataTokens.GetToken(assembly.IsNil ? Assembly : assembly));
        String(p, name); Constant(p, size);
    }

    public void Blob(string name, int size)
    {
        var p = _payloads["Blobs"];
        String(p, name); Constant(p, size);
    }

    public byte[] Build()
    {
        _metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, _metadata.GetOrAddString("<Module>"),
            default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var il = new BlobBuilder();
        var encoder = new MethodBodyStreamEncoder(il);
        foreach (var (name, payload) in _payloads)
        {
            if (Version < new Version(2, 1) && name is "RvaFields" or "FrozenObjects" or "ManifestResources")
                continue;
            // The payload is data for the parser, not executable IL. Do not append ret.
            int offset = encoder.AddMethodBody(new InstructionEncoder(payload), maxStack: 8);
            _metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static,
                MethodImplAttributes.IL, _metadata.GetOrAddString(name),
                _metadata.GetOrAddBlob(new byte[] { 0, 0, 1 }), offset, MetadataTokens.ParameterHandle(1));
        }
        var pe = new BlobBuilder();
        new NamesSectionPeBuilder(_metadata, il, _names).Serialize(pe);
        return pe.ToArray();
    }

    public MstatData Read()
    {
        byte[] bytes = Build();
        using var stream = new MemoryStream(bytes, writable: false);
        return MstatData.Read(stream, bytes.Length);
    }

    // Deliberately never falls back to full ldc.i4: compact-opcode tests pin the emitted IL.
    private static void CompactConstant(BlobBuilder payload, int value)
    {
        if (value is >= -1 and <= 8)
            payload.WriteByte((byte)(0x16 + value)); // ldc.i4.m1 through ldc.i4.8
        else if (value is >= sbyte.MinValue and <= sbyte.MaxValue)
        {
            payload.WriteByte(0x1f); // ldc.i4.s
            payload.WriteByte(unchecked((byte)(sbyte)value));
        }
        else
            throw new ArgumentOutOfRangeException(nameof(value));
    }

    public void TypeWithCompactConstants(EntityHandle handle, int size, int nodeId = 0)
    {
        var p = _payloads["Types"];
        Token(p, handle); CompactConstant(p, size);
        if (Version.Major >= 2) CompactConstant(p, nodeId);
    }

    public void MethodWithCompactConstants(EntityHandle handle, int code, int gc = 0, int unwind = 0, int nodeId = 0)
    {
        var p = _payloads["Methods"];
        Token(p, handle); CompactConstant(p, code); CompactConstant(p, gc); CompactConstant(p, unwind);
        if (Version.Major >= 2) CompactConstant(p, nodeId);
    }
}

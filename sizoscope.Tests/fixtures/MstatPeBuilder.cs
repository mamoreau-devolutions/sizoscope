using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Sizoscope.Tests.Fixtures;

internal sealed class MstatPeBuilder
{
    private readonly MetadataBuilder _metadata = new();
    private readonly BlobBuilder _names = new();
    private readonly Dictionary<string, BlobBuilder> _payloads = new()
    {
        ["Types"] = new(),
        ["Methods"] = new(),
        ["Blobs"] = new(),
        ["RvaFields"] = new(),
        ["FrozenObjects"] = new(),
        ["ManifestResources"] = new(),
    };

    public MstatPeBuilder(string version = "2.1.0.0")
    {
        _metadata.AddModule(
            0,
            _metadata.GetOrAddString("fixture.mstat"),
            _metadata.GetOrAddGuid(new Guid("026ab527-39e7-419b-939c-d8f851663df4")),
            default,
            default);
        _metadata.AddAssembly(
            _metadata.GetOrAddString("MstatFixture"),
            new Version(version),
            default,
            default,
            0,
            AssemblyHashAlgorithm.None);
        Assembly = AddAssembly("Fixture.Library");
    }

    public AssemblyReferenceHandle Assembly { get; }

    public AssemblyReferenceHandle AddAssembly(string name, string culture = null) =>
        _metadata.AddAssemblyReference(
            _metadata.GetOrAddString(name),
            new Version(1, 0, 0, 0),
            culture is null ? default : _metadata.GetOrAddString(culture),
            default,
            0,
            default);

    public TypeReferenceHandle AddType(
        string name,
        string ns = "Example",
        EntityHandle scope = default) =>
        _metadata.AddTypeReference(
            scope.IsNil ? Assembly : scope,
            _metadata.GetOrAddString(ns),
            _metadata.GetOrAddString(name));

    public MemberReferenceHandle AddMethod(string name, EntityHandle owner, bool generic = false)
    {
        var signature = new BlobBuilder();
        signature.WriteByte(generic ? (byte)0x10 : (byte)0);
        if (generic)
            signature.WriteCompressedInteger(1);
        signature.WriteCompressedInteger(0);
        signature.WriteByte(0x01);
        return _metadata.AddMemberReference(
            owner,
            _metadata.GetOrAddString(name),
            _metadata.GetOrAddBlob(signature));
    }

    public MemberReferenceHandle AddField(string name, EntityHandle owner) =>
        _metadata.AddMemberReference(
            owner,
            _metadata.GetOrAddString(name),
            _metadata.GetOrAddBlob(new byte[] { 0x06, 0x08 }));

    public TypeSpecificationHandle AddTypeSpec(TypeReferenceHandle definition, byte argument)
    {
        var signature = new BlobBuilder();
        signature.WriteByte(0x15);
        signature.WriteByte(0x12);
        signature.WriteCompressedInteger((MetadataTokens.GetRowNumber(definition) << 2) | 1);
        signature.WriteCompressedInteger(1);
        signature.WriteByte(argument);
        return _metadata.AddTypeSpecification(_metadata.GetOrAddBlob(signature));
    }

    public MethodSpecificationHandle AddMethodSpec(MemberReferenceHandle definition, byte argument) =>
        _metadata.AddMethodSpecification(
            definition,
            _metadata.GetOrAddBlob(new byte[] { 0x0a, 1, argument }));

    public int AddName(string name)
    {
        int offset = _names.Count;
        _names.WriteSerializedString(name);
        return offset;
    }

    public void Type(EntityHandle handle, int size, int nodeId = 0)
    {
        BlobBuilder payload = _payloads["Types"];
        Token(payload, handle);
        Constant(payload, size);
        Constant(payload, nodeId);
    }

    public void Method(
        EntityHandle handle,
        int code,
        int gc = 0,
        int unwind = 0,
        int nodeId = 0)
    {
        BlobBuilder payload = _payloads["Methods"];
        Token(payload, handle);
        Constant(payload, code);
        Constant(payload, gc);
        Constant(payload, unwind);
        Constant(payload, nodeId);
    }

    public void Field(MemberReferenceHandle handle, int size, int nodeId = 0)
    {
        BlobBuilder payload = _payloads["RvaFields"];
        Token(payload, handle);
        Constant(payload, size);
        Constant(payload, nodeId);
    }

    public void Frozen(EntityHandle type, int size, int nodeId, EntityHandle owner = default)
    {
        BlobBuilder payload = _payloads["FrozenObjects"];
        Token(payload, type);
        Constant(payload, size);
        Constant(payload, nodeId);
        if (owner.IsNil)
            payload.WriteByte(0);
        else
            Token(payload, owner);
    }

    public void Resource(string name, int size)
    {
        BlobBuilder payload = _payloads["ManifestResources"];
        Constant(payload, MetadataTokens.GetToken(Assembly));
        String(payload, name);
        Constant(payload, size);
    }

    public void Blob(string name, int size)
    {
        BlobBuilder payload = _payloads["Blobs"];
        String(payload, name);
        Constant(payload, size);
    }

    public void Folded(EntityHandle folded, params EntityHandle[] keptBodies)
    {
        BlobBuilder payload = GetDeduplicatedMethodsPayload();
        Token(payload, folded);
        Constant(payload, keptBodies.Length);
        foreach (EntityHandle keptBody in keptBodies)
        {
            Token(payload, keptBody);
            Constant(payload, 0);
        }
    }

    public void MalformedFoldedCount(EntityHandle folded, int targetCount)
    {
        BlobBuilder payload = GetDeduplicatedMethodsPayload();
        Token(payload, folded);
        Constant(payload, targetCount);
    }

    public byte[] Build()
    {
        _metadata.AddTypeDefinition(
            TypeAttributes.NotPublic,
            default,
            _metadata.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));

        var il = new BlobBuilder();
        var encoder = new MethodBodyStreamEncoder(il);
        foreach ((string name, BlobBuilder payload) in _payloads)
        {
            int offset = encoder.AddMethodBody(new InstructionEncoder(payload), maxStack: 8);
            _metadata.AddMethodDefinition(
                MethodAttributes.Public | MethodAttributes.Static,
                MethodImplAttributes.IL,
                _metadata.GetOrAddString(name),
                _metadata.GetOrAddBlob(new byte[] { 0, 0, 1 }),
                offset,
                MetadataTokens.ParameterHandle(1));
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

    private static void Token(BlobBuilder payload, EntityHandle handle)
    {
        payload.WriteByte(0xd0);
        payload.WriteInt32(MetadataTokens.GetToken(handle));
    }

    private static void Constant(BlobBuilder payload, int value)
    {
        payload.WriteByte(0x20);
        payload.WriteInt32(value);
    }

    private BlobBuilder GetDeduplicatedMethodsPayload()
    {
        if (!_payloads.TryGetValue(
            "DeduplicatedMethods",
            out BlobBuilder payload))
        {
            _payloads.Add("DeduplicatedMethods", payload = new BlobBuilder());
        }

        return payload;
    }

    private void String(BlobBuilder payload, string value)
    {
        payload.WriteByte(0x72);
        payload.WriteInt32(MetadataTokens.GetToken(_metadata.GetOrAddUserString(value)));
    }
}

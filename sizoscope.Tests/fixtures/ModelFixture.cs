namespace Sizoscope.Tests.Fixtures;

internal static class ModelFixture
{
    public static MstatPeBuilder Create(bool reordered = false, byte argument = 0x08,
        string resourceName = "payload", string frozenName = "LooseObject")
    {
        var b = new MstatPeBuilder(padding: reordered);
        if (reordered) b.AddName("Name padding changes byte offsets");
        var companion = b.AddAssembly("Companion.Library");
        var other = reordered ? b.AddType("Other", scope: companion) : default;
        var generic = reordered ? b.AddType("Generic`1") : default;
        var owner = b.AddType("Owner");
        var child = b.AddType("Child", "", owner);
        if (!reordered)
        {
            generic = b.AddType("Generic`1");
            other = b.AddType("Other", scope: companion);
        }
        var spec = b.AddTypeSpec(generic, argument);
        var method = b.AddMethod("Run", owner);
        var field = b.AddField("State", owner);
        var specMethod = b.AddMethod("Build", spec);
        var definition = b.AddMethod("Create", generic, generic: true);
        var instantiation = b.AddMethodSpec(definition, argument);
        var otherMethod = b.AddMethod("Work", other);
        b.Type(owner, 11, b.AddName("Owner"));
        b.Type(child, 13, b.AddName("Child"));
        b.Type(generic, 17, b.AddName("Generic"));
        b.Type(spec, 41, b.AddName("Spec"));
        b.Type(other, 61, b.AddName("Other"));
        b.Method(method, 19, 23, 29, b.AddName("Run"));
        b.Field(field, 31, b.AddName("State"));
        b.Method(specMethod, 43, nodeId: b.AddName("Build"));
        b.Method(definition, 47, nodeId: b.AddName("Create"));
        b.Method(instantiation, 53, nodeId: b.AddName("CreateSpec"));
        b.Method(otherMethod, 67, nodeId: b.AddName("Work"));
        b.Frozen(owner, 37, b.AddName("OwnerObject"), owner);
        b.Frozen(spec, 59, b.AddName("SpecObject"), spec);
        b.Frozen(other, 73, b.AddName(frozenName));
        b.Resource(resourceName, 71);
        b.Blob("Metadata", 79);
        return b;
    }

    public static IEnumerable<MstatData.MstatTypeDefinition> Types(MstatData data) =>
        data.GetScopes().SelectMany(a => a.GetTypes()).SelectMany(WithNested);

    private static IEnumerable<MstatData.MstatTypeDefinition> WithNested(MstatData.MstatTypeDefinition type)
    {
        yield return type;
        foreach (var child in type.GetNestedTypes())
            foreach (var descendant in WithNested(child))
                yield return descendant;
    }

    public static IEnumerable<MstatData.MstatTypeSpecification> Specs(MstatData data) =>
        Types(data).SelectMany(t => t.GetTypeSpecifications());

    public static IEnumerable<MstatData.MstatMemberDefinition> Members(MstatData data) =>
        Types(data).SelectMany(t => t.GetMembers()).Concat(Specs(data).SelectMany(s => s.GetMembers()));

    public static IEnumerable<MstatData.MstatMethodSpecification> MethodSpecs(MstatData data) =>
        Members(data).SelectMany(m => m.GetInstantiations());

    public static IEnumerable<MstatData.MstatManifestResource> Resources(MstatData data) =>
        data.GetScopes().SelectMany(a => a.GetManifestResources());

    public static IEnumerable<MstatData.MstatFrozenObject> Frozen(MstatData data) =>
        data.GetFrozenObjects().Concat(Types(data).SelectMany(t => t.GetFrozenObjects()))
            .Concat(Specs(data).SelectMany(s => s.GetFrozenObjects()));
}

using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Sizoscope.Tests.Fixtures;

internal sealed class NamesSectionPeBuilder : ManagedPEBuilder
{
    private readonly BlobBuilder _names;

    public NamesSectionPeBuilder(MetadataBuilder metadata, BlobBuilder il, BlobBuilder names)
        : base(
            new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata),
            il,
            deterministicIdProvider: _ => new BlobContentId(
                new Guid("072c8adc-f31b-4224-92ec-e1e1670b0384"), 0))
    {
        _names = names;
    }

    protected override ImmutableArray<Section> CreateSections() =>
        base.CreateSections().Add(new Section(
            ".names",
            SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead));

    protected override BlobBuilder SerializeSection(string name, SectionLocation location) =>
        name == ".names" ? _names : base.SerializeSection(name, location);
}

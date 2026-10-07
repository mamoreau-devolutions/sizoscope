using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;

public partial class MstatData
{
    // MSTAT 2.2+ "DeduplicatedMethods": identical method bodies folded by ILC.
    // Each record is: ldtoken <folded method>, count, then count x
    // (ldtoken <kept body>, mangled name offset).
    private EntityHandle[] _deduplicatedBodies = [];
    private int[] _deduplicatedAliasStarts = [];
    private EntityHandle[] _deduplicatedAliases = [];
    private long _deduplicatedSavedSize;

    public int DeduplicatedBodyCount => _deduplicatedBodies.Length;
    public int DeduplicatedAliasCount => _deduplicatedAliases.Length;

    /// <summary>
    /// Gets the estimated bytes saved by method body folding. The estimate is the
    /// size of each kept body multiplied by the number of methods folded into it
    /// and is not part of <see cref="Size"/>.
    /// </summary>
    public long DeduplicatedSavedSize => _deduplicatedSavedSize;

    public IEnumerable<MstatDeduplicatedBody> GetDeduplicatedBodies()
    {
        for (int i = 0; i < _deduplicatedBodies.Length; i++)
            yield return new MstatDeduplicatedBody(this, i);
    }

    private bool TryGetGlobalMethod(string name, out MethodDefinition method)
    {
        TypeDefinition globalType =
            _reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(1));
        foreach (MethodDefinitionHandle methodHandle in globalType.GetMethods())
        {
            method = _reader.GetMethodDefinition(methodHandle);
            if (_reader.StringComparer.Equals(method.Name, name))
                return true;
        }

        method = default;
        return false;
    }

    private void ParseDeduplicatedMethods()
    {
        if (!TryGetGlobalMethod("DeduplicatedMethods", out MethodDefinition method))
            return;

        BlobReader reader =
            _peReader.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
        var aliasesByBody = new Dictionary<EntityHandle, List<EntityHandle>>();
        int aliasCount = 0;
        while (reader.RemainingBytes > 0)
        {
            EntityHandle foldedMethod = reader.ILReadLdToken();
            int targetCount = reader.ILReadI4Constant();
            if (targetCount < 0)
            {
                throw new BadImageFormatException(
                    $"Invalid deduplicated method count {targetCount}");
            }

            for (int i = 0; i < targetCount; i++)
            {
                EntityHandle keptBody = reader.ILReadLdToken();
                reader.ILReadI4Constant(); // Mangled name offset into .names.
                if (!aliasesByBody.TryGetValue(
                    keptBody,
                    out List<EntityHandle> aliasesForBody))
                {
                    aliasesByBody.Add(
                        keptBody,
                        aliasesForBody = new List<EntityHandle>());
                }

                aliasesForBody.Add(foldedMethod);
                aliasCount++;
            }
        }

        var bodies = new List<EntityHandle>(aliasesByBody.Count);
        var starts = new List<int>(aliasesByBody.Count);
        var aliases = new List<EntityHandle>(aliasCount);
        long savedSize = 0;
        foreach ((EntityHandle body, List<EntityHandle> bodyAliases) in aliasesByBody)
        {
            bodies.Add(body);
            starts.Add(aliases.Count);
            aliases.AddRange(bodyAliases);
            savedSize = checked(
                savedSize + GetMethodBodySize(body) * bodyAliases.Count);
        }

        _deduplicatedBodies = bodies.ToArray();
        _deduplicatedAliasStarts = starts.ToArray();
        _deduplicatedAliases = aliases.ToArray();
        _deduplicatedSavedSize = savedSize;
    }

    private long GetMethodBodySize(EntityHandle handle) => handle.Kind switch
    {
        HandleKind.MemberReference =>
            _memberRefCache[MetadataTokens.GetRowNumber(handle)].Size,
        HandleKind.MethodSpecification =>
            _methodSpecCache[MetadataTokens.GetRowNumber(handle)].Size,
        _ => 0,
    };

    private string FormatMethod(EntityHandle handle)
    {
        MemberReferenceHandle memberReference = handle.Kind switch
        {
            HandleKind.MemberReference => (MemberReferenceHandle)handle,
            HandleKind.MethodSpecification => (MemberReferenceHandle)_reader
                .GetMethodSpecification((MethodSpecificationHandle)handle).Method,
            _ => default,
        };
        if (memberReference.IsNil)
            return handle.Kind.ToString();

        var builder = new StringBuilder();
        _formatter.FormatName(
            builder,
            _reader.GetMemberReference(memberReference).Parent).Append('.');
        return (handle.Kind == HandleKind.MethodSpecification
            ? _formatter.FormatMember(
                builder,
                (MethodSpecificationHandle)handle)
            : _formatter.FormatMember(builder, memberReference)).ToString();
    }

    public readonly struct MstatDeduplicatedBody
    {
        private readonly MstatData _data;
        private readonly int _index;

        internal MstatDeduplicatedBody(MstatData data, int index) =>
            (_data, _index) = (data, index);

        private int AliasStart => _data._deduplicatedAliasStarts[_index];

        public int AliasCount =>
            (_index + 1 < _data._deduplicatedAliasStarts.Length
                ? _data._deduplicatedAliasStarts[_index + 1]
                : _data._deduplicatedAliases.Length) - AliasStart;

        public long Size =>
            _data.GetMethodBodySize(_data._deduplicatedBodies[_index]);

        public long SavedSize => checked(Size * AliasCount);

        public string Name =>
            _data.FormatMethod(_data._deduplicatedBodies[_index]);

        public IEnumerable<string> GetAliasNames()
        {
            for (int i = 0; i < AliasCount; i++)
            {
                yield return _data.FormatMethod(
                    _data._deduplicatedAliases[AliasStart + i]);
            }
        }

        public override string ToString() => Name;
    }
}

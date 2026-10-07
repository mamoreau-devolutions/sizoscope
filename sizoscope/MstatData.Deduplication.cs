using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;

public partial class MstatData
{
    // MSTAT 2.2+ "DeduplicatedMethods": identical method bodies folded by ILC (ICF).
    // Each record is: ldtoken <folded method>, count, then count x (ldtoken <kept body>, mangled name offset).
    private EntityHandle[] _dedupBodies = [];
    private int[] _dedupAliasStart = [];
    private EntityHandle[] _dedupAliases = [];
    private long _dedupSavedSize;

    public int DeduplicatedBodyCount => _dedupBodies.Length;
    public int DeduplicatedAliasCount => _dedupAliases.Length;

    /// <summary>
    /// Estimated bytes saved by method body folding: for each kept body, its size times the
    /// number of methods that were folded into it. Not part of <see cref="Size"/>.
    /// </summary>
    public long DeduplicatedSavedSize => _dedupSavedSize;

    public IEnumerable<MstatDeduplicatedBody> GetDeduplicatedBodies()
    {
        for (int i = 0; i < _dedupBodies.Length; i++)
            yield return new MstatDeduplicatedBody(this, i);
    }

    private bool TryGetGlobalMethod(string name, out MethodDefinition method)
    {
        TypeDefinition globalType = _reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(1));
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

        BlobReader reader = _peReader.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
        var targets = new Dictionary<EntityHandle, List<EntityHandle>>();
        int aliasCount = 0;
        while (reader.RemainingBytes > 0)
        {
            EntityHandle folded = reader.ILReadLdToken();
            int count = reader.ILReadI4Constant();
            if (count < 0)
                throw new BadImageFormatException($"Invalid deduplicated method count {count}");
            for (int i = 0; i < count; i++)
            {
                EntityHandle target = reader.ILReadLdToken();
                reader.ILReadI4Constant(); // mangled name offset into .names
                if (!targets.TryGetValue(target, out List<EntityHandle> list))
                    targets.Add(target, list = new List<EntityHandle>());
                list.Add(folded);
                aliasCount++;
            }
        }

        var bodies = new List<EntityHandle>(targets.Count);
        var starts = new List<int>(targets.Count);
        var aliases = new List<EntityHandle>(aliasCount);
        long saved = 0;
        foreach (var (target, list) in targets)
        {
            bodies.Add(target);
            starts.Add(aliases.Count);
            aliases.AddRange(list);
            saved += GetMethodBodySize(target) * list.Count;
        }
        _dedupBodies = bodies.ToArray();
        _dedupAliasStart = starts.ToArray();
        _dedupAliases = aliases.ToArray();
        _dedupSavedSize = saved;
    }

    private long GetMethodBodySize(EntityHandle handle) => handle.Kind switch
    {
        HandleKind.MemberReference => _memberRefCache[MetadataTokens.GetRowNumber(handle)].Size,
        HandleKind.MethodSpecification => _methodSpecCache[MetadataTokens.GetRowNumber(handle)].Size,
        _ => 0,
    };

    private string FormatMethod(EntityHandle handle)
    {
        MemberReferenceHandle memberRef = handle.Kind switch
        {
            HandleKind.MemberReference => (MemberReferenceHandle)handle,
            HandleKind.MethodSpecification => (MemberReferenceHandle)_reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method,
            _ => default,
        };
        if (memberRef.IsNil)
            return handle.Kind.ToString();

        var sb = new StringBuilder();
        _formatter.FormatName(sb, _reader.GetMemberReference(memberRef).Parent).Append('.');
        return (handle.Kind == HandleKind.MethodSpecification
            ? _formatter.FormatMember(sb, (MethodSpecificationHandle)handle)
            : _formatter.FormatMember(sb, memberRef)).ToString();
    }
    public readonly struct MstatDeduplicatedBody
    {
        private readonly MstatData _data;
        private readonly int _index;

        internal MstatDeduplicatedBody(MstatData data, int index) => (_data, _index) = (data, index);

        private int AliasStart => _data._dedupAliasStart[_index];
        public int AliasCount => (_index + 1 < _data._dedupAliasStart.Length ? _data._dedupAliasStart[_index + 1] : _data._dedupAliases.Length) - AliasStart;
        public long Size => _data.GetMethodBodySize(_data._dedupBodies[_index]);
        public long SavedSize => Size * AliasCount;
        public string Name => _data.FormatMethod(_data._dedupBodies[_index]);

        public IEnumerable<string> GetAliasNames()
        {
            for (int i = 0; i < AliasCount; i++)
                yield return _data.FormatMethod(_data._dedupAliases[AliasStart + i]);
        }

        public override string ToString() => Name;
    }
}

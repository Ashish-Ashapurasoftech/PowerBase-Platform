using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Common.Services;

public class FieldNameResolver : IFieldNameResolver
{
    private readonly IAppFieldRepository _fieldRepo;

    public FieldNameResolver(IAppFieldRepository fieldRepo) => _fieldRepo = fieldRepo;

    public async Task<string> GenerateUniqueNameAsync(long tableId, string label, bool isSystem, CancellationToken ct = default)
    {
        var baseName = FieldNaming.GenerateBaseName(label, isSystem);

        var candidate = baseName;
        var suffix = 2;
        while (await _fieldRepo.NameExistsInTableAsync(tableId, candidate, ct))
        {
            candidate = $"{baseName}{suffix}";
            suffix++;
        }

        return candidate;
    }
}

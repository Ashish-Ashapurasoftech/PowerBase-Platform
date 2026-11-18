using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships.Queries;

/// <summary>Tells whether one parent record passes a Reference field's dropdown filter, i.e. whether
/// the picker would offer it. A single exists-check (no labels, no row page) built from the same
/// <see cref="ReferenceFilterResolver"/> clauses the picker and the save-time validator use, so the
/// three can never disagree. A reference field with no filter allows every live parent record.</summary>
public class IsParentAllowedQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRelationshipRepository _relRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IQueryContext _queryContext;
    private readonly IUserRepository? _userRepo;
    private readonly IAppUserRepository? _appUserRepo;

    public IsParentAllowedQueryHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRelationshipRepository relRepo,
        IRecordRepository recordRepo,
        IQueryContext queryContext,
        IUserRepository? userRepo = null,
        IAppUserRepository? appUserRepo = null)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _relRepo = relRepo;
        _recordRepo = recordRepo;
        _queryContext = queryContext;
        _userRepo = userRepo;
        _appUserRepo = appUserRepo;
    }

    /// <param name="parentRecord">The parent's row Id (Record ID#), its PublicId, or the key text a Reference reads back
    /// as when its parent shows a key (resolved as the save check resolves it — see <see cref="ReferenceWriteValidator"/>).</param>
    /// <param name="filterValues">Current form values of controlling fields (fid → value); empty when the
    /// check runs outside a form, in which case conditions on form fields can't be judged.</param>
    public async Task<bool> HandleAsync(
        Guid relationshipPublicId, string parentRecord,
        IReadOnlyDictionary<int, string?>? filterValues = null, CancellationToken ct = default)
    {
        var rel = await _relRepo.GetByPublicIdAsync(relationshipPublicId, ct)
            ?? throw new NotFoundException("Relationship", relationshipPublicId);

        var parent = await _tableRepo.GetByIdAsync(rel.ParentTableId, ct);

        var parentRowId = await ParentRecordResolver.ResolveRowIdAsync(rel, parent, parentRecord, _recordRepo, _fieldRepo, ct);
        if (parentRowId <= 0 || !await _recordRepo.ExistsAsync(parent, parentRowId, ct)) return false;

        var childFields = await _fieldRepo.ListByTableAsync(rel.ChildTableId, ct);
        var referenceField = childFields.FirstOrDefault(f => f.Id == rel.ReferenceFieldId);
        var settings = FormulaTypeMap.ParseReferenceSettings(referenceField?.Settings);
        if (!ReferenceFilterResolver.HasFilter(settings)) return true;

        var parentFields = await _fieldRepo.ListByTableAsync(parent.Id, ct);
        var clauses = await ReferenceFilterResolver.BuildAsync(
            settings, childFields, parentFields,
            filterValues ?? new Dictionary<int, string?>(), _tableRepo, _fieldRepo, ct, _queryContext.UserId, _relRepo, _recordRepo,
            _userRepo is null ? null : new ReferenceFilterUsers(_userRepo, _appUserRepo, _queryContext.UserEmail, _queryContext.UserName));
        if (clauses.Count == 0) return true;   // nothing applicable without form values

        return await _recordRepo.MatchesReferenceFilterAsync(parent, parentRowId, clauses, ct);
    }
}

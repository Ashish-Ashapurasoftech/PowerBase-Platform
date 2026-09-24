using PowerBase.Application.Forms;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IFormRuleRepository
{
    Task<FormRule> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<long> GetAppIdByPublicIdAsync(Guid rulePublicId, CancellationToken ct = default);
    Task<IReadOnlyList<FormRule>> ListByFormAsync(long formId, CancellationToken ct = default);
    /// <summary>Every active rule across every form on the table that has at least one
    /// Require/PreventSave/Enable/Disable/ChangeValue/DisplayMessage action — the candidate list
    /// for a report's Grid Edit rule picker (see FormRuleGridEditCandidate's doc comment).</summary>
    Task<IReadOnlyList<FormRuleGridEditCandidate>> ListGridEditCandidatesByTableIdAsync(long appTableId, CancellationToken ct = default);
    /// <summary>Every active, non-deleted rule across every form attached to this table — used by
    /// server-side write-path enforcement, which has no "which form" context (a direct API write
    /// isn't tied to any one form), so it must enforce the full set for the table being written.</summary>
    Task<IReadOnlyList<FormRule>> ListActiveByTableIdAsync(long appTableId, CancellationToken ct = default);
    Task<(long Id, Guid PublicId)> CreateAsync(FormRule rule, CancellationToken ct = default);
    Task SaveRuleBodyAsync(Guid publicId, string name, string? description, string? tags,
        bool isActive, string runTrigger, string conditionLogic, bool isExpressionMode,
        string? expressionText, IReadOnlyList<FormRuleCondition> conditions,
        IReadOnlyList<FormRuleAction> actions, byte[] rowVersion, CancellationToken ct = default);
    Task<int> DeleteAsync(Guid publicId, CancellationToken ct = default);
    Task ReorderAsync(long formId, IReadOnlyList<Guid> orderedPublicIds, CancellationToken ct = default);
    Task<int> SetActiveAsync(Guid publicId, bool isActive, CancellationToken ct = default);
    Task<(long Id, Guid PublicId)> DuplicateAsync(Guid sourcePublicId, string newName, long userId, CancellationToken ct = default);
}

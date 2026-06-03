using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IFormRuleRepository
{
    Task<FormRule> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<long> GetAppIdByPublicIdAsync(Guid rulePublicId, CancellationToken ct = default);
    Task<IReadOnlyList<FormRule>> ListByFormAsync(long formId, CancellationToken ct = default);
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

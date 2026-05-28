namespace PowerBase.API.Models.Forms;

public class ReorderFormRulesRequest
{
    public List<Guid> OrderedRuleIds { get; init; } = [];
}

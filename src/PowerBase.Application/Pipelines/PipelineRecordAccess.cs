using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Pipelines;

/// <summary>What a PowerFlow record step needs to do to a table.</summary>
public enum PipelineRecordAccessKind { View, Add, Modify, Delete, AddAndModify }

/// <summary>
/// One rule set for "may this identity run this record step on this table", shared by the save-time
/// validators and the run-time guard. Record data access is governed by the role's table-level access
/// (the same model the Records API uses), not by the flat records:* permission codes, which regular
/// roles do not carry.
/// </summary>
public static class PipelineRecordAccess
{
    public static bool IsAllowed(TableAccessContext access, PipelineRecordAccessKind kind)
    {
        if (access.Unrestricted) return true;
        return kind switch
        {
            PipelineRecordAccessKind.View => access.CanView,
            PipelineRecordAccessKind.Add => access.CanAdd,
            PipelineRecordAccessKind.Modify => access.ModifyScope != RecordScopes.None,
            PipelineRecordAccessKind.Delete => access.CanDelete,
            PipelineRecordAccessKind.AddAndModify => access.CanAdd && access.ModifyScope != RecordScopes.None,
            _ => true
        };
    }

    public static string Describe(PipelineRecordAccessKind kind, string tableName) => kind switch
    {
        PipelineRecordAccessKind.View => $"You do not have permission to view records in table '{tableName}'.",
        PipelineRecordAccessKind.Add => $"You do not have permission to add records to table '{tableName}'.",
        PipelineRecordAccessKind.Modify => $"You do not have permission to modify records in table '{tableName}'.",
        PipelineRecordAccessKind.Delete => $"You do not have permission to delete records from table '{tableName}'.",
        _ => $"You do not have permission to add and modify records in table '{tableName}'."
    };
}

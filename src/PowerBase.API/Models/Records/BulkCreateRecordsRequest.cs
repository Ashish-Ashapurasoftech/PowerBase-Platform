using System.Text.Json;

namespace PowerBase.API.Models.Records;

public class BulkCreateRecordsRequest
{
    /// <summary>The new records, in the order the user entered them (at most 500). Each one's
    /// <c>fields</c> holds only the cells the user filled, keyed by Fid — a field left out gets its
    /// default value, as in a single-record create.</summary>
    public List<BulkCreateRecordItem> Records { get; set; } = new();

    /// <summary>The report the rows were added from (its Grid Edit). With <see cref="IsGridEditSave"/>,
    /// Form Rule checks narrow to that report's Grid Edit rules — see UpdateRecordRequest.ReportId.</summary>
    public Guid? ReportId { get; set; }

    /// <summary>True only for the report grid's own Grid Edit save — see UpdateRecordRequest.IsGridEditSave.</summary>
    public bool IsGridEditSave { get; set; }
}

public class BulkCreateRecordItem
{
    public Dictionary<string, JsonElement> Fields { get; set; } = new();
}

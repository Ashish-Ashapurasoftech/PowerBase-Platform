using System.ComponentModel.DataAnnotations;

namespace PowerBase.API.Models.Fields;

public class BulkDeleteFieldsRequest
{
    [Required]
    public IEnumerable<Guid> FieldIds { get; set; } = [];
}

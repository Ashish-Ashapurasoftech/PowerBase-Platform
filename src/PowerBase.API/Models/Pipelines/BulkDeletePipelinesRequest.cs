using System;
using System.Collections.Generic;

namespace PowerBase.API.Models.Pipelines;

public class BulkDeletePipelinesRequest
{
    public List<Guid> PublicIds { get; set; } = [];
}

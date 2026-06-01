using System;
using System.Collections.Generic;

namespace PowerBase.API.Models.Apps;

public record UpdateReportVisibilityMatrixRequest(
    List<ReportVisibilityUpdateDto> Updates);

public record ReportVisibilityUpdateDto(
    Guid ReportPublicId,
    string Visibility,
    List<Guid> VisibleToRoleIds);

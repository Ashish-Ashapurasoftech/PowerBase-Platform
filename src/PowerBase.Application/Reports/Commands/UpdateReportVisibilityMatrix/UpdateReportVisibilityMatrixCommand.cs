using System;
using System.Collections.Generic;

namespace PowerBase.Application.Reports.Commands.UpdateReportVisibilityMatrix;

public record UpdateReportVisibilityMatrixCommand(
    Guid AppPublicId,
    List<ReportVisibilityUpdate> Updates);

public record ReportVisibilityUpdate(
    Guid ReportPublicId,
    string Visibility,
    List<Guid> VisibleToRoleIds);

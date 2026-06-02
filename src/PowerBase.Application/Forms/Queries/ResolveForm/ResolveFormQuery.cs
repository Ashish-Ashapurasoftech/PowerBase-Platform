using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Forms.Queries.ResolveForm;

public record ResolveFormQuery(Guid TableId, string Mode, Guid? ReportId);

public class ResolveFormQueryHandler
{
    private readonly IFormRepository _formRepo;
    private readonly IReportRepository _reportRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IQueryContext _queryContext;

    public ResolveFormQueryHandler(IFormRepository formRepo, IReportRepository reportRepo, IAppUserRepository appUserRepo, IQueryContext queryContext)
    {
        _formRepo = formRepo;
        _reportRepo = reportRepo;
        _appUserRepo = appUserRepo;
        _queryContext = queryContext;
    }

    public async Task<FormDetail?> HandleAsync(ResolveFormQuery request, CancellationToken ct)
    {
        var forms = await _formRepo.ListByTableAsync(request.TableId, ct);
        if (!forms.Any()) return null;

        // 1. Report override
        if (request.ReportId.HasValue)
        {
            try
            {
                var report = await _reportRepo.GetByPublicIdAsync(request.ReportId.Value, ct);
                if (report.ViewEditFormId.HasValue)
                {
                    var reportForm = forms.FirstOrDefault(f => f.Id == report.ViewEditFormId.Value);
                    if (reportForm != null) return MapToDetail(reportForm);
                }
            }
            catch
            {
                // Ignore if report not found
            }
        }

        // 2. Role override
        var roleOverrides = await _formRepo.GetRoleFormOverridesAsync(request.TableId, ct);

        Guid? userRoleId = null;
        try
        {
            var appId = await _formRepo.GetAppIdByPublicIdAsync(forms.First().PublicId, ct);
            userRoleId = await _appUserRepo.GetUserRolePublicIdAsync(appId, _queryContext.UserId, ct);
        }
        catch
        {
            // Ignore if user not found in app
        }

        Guid? overrideFormPublicId = null;

        // Try user's specific role override
        if (userRoleId.HasValue)
        {
            var userRoleOverride = roleOverrides.FirstOrDefault(r => r.RolePublicId == userRoleId.Value);
            if (userRoleOverride != default)
            {
                overrideFormPublicId = request.Mode.ToLowerInvariant() == "edit" || request.Mode.ToLowerInvariant() == "view"
                    ? userRoleOverride.EditFormPublicId
                    : userRoleOverride.AddFormPublicId;
            }
        }

        // Try "Everyone" override (RolePublicId is null) if still no form found
        if (!overrideFormPublicId.HasValue)
        {
            var everyoneOverride = roleOverrides.FirstOrDefault(r => r.RolePublicId == null);
            if (everyoneOverride != default)
            {
                overrideFormPublicId = request.Mode.ToLowerInvariant() == "edit" || request.Mode.ToLowerInvariant() == "view"
                    ? everyoneOverride.EditFormPublicId
                    : everyoneOverride.AddFormPublicId;
            }
        }

        // If we found an override form, return it
        if (overrideFormPublicId.HasValue)
        {
            var overrideForm = forms.FirstOrDefault(f => f.PublicId == overrideFormPublicId.Value);
            if (overrideForm != null) return MapToDetail(overrideForm);
        }

        // 3. Fallback to default
        var resolved = forms.FirstOrDefault(f => f.IsDefault) ?? forms.FirstOrDefault();
        return resolved == null ? null : MapToDetail(resolved);
    }

    private static FormDetail MapToDetail(Form f) => new()
    {
        Id               = f.PublicId,
        Name             = f.Name,
        IsDefault        = f.IsDefault,
        AutoAddNewFields = f.AutoAddNewFields,
        ShowBuiltInFields = f.ShowBuiltInFields,
        SaveOptions      = f.SaveOptions,
        DisplayOrder     = f.DisplayOrder,
        CreatedOn        = f.CreatedOn,
        RowVersion       = f.RowVersion,
    };
}

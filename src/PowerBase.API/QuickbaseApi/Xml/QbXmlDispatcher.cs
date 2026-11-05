namespace PowerBase.API.QuickbaseApi.Xml;

/// <summary>Routes a Quickbase XML action name (case-insensitive) to the code that serves it.</summary>
public sealed class QbXmlDispatcher
{
    private readonly IServiceProvider _services;
    public QbXmlDispatcher(IServiceProvider services) => _services = services;

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    public Task<QbXmlResponse> DispatchAsync(QbXmlRequest req, CancellationToken ct) => req.Action.ToUpperInvariant() switch
    {
        "API_ADDRECORD" => Get<QbXmlRecordActions>().AddRecordAsync(req, ct),
        "API_EDITRECORD" => Get<QbXmlRecordActions>().EditRecordAsync(req, ct),
        "API_DELETERECORD" => Get<QbXmlRecordActions>().DeleteRecordAsync(req, ct),
        "API_GETFIELDPROPERTIES" => Get<QbXmlRecordActions>().GetFieldPropertiesAsync(req, ct),
        "API_UPLOADFILE" => Get<QbXmlRecordActions>().UploadFileAsync(req, ct),
        "API_GETUSERINFO" => Get<QbXmlUserActions>().GetUserInfoAsync(req, ct),
        "API_GETUSERROLE" => Get<QbXmlUserActions>().GetUserRoleAsync(req, ct),
        "API_USERROLES" => Get<QbXmlUserActions>().UserRolesAsync(req, ct),
        "API_ADDUSERTOROLE" => Get<QbXmlUserActions>().AddUserToRoleAsync(req, ct),
        "API_PROVISIONUSER" => Get<QbXmlUserActions>().ProvisionUserAsync(req, ct),
        "API_SENDINVITATION" => Get<QbXmlUserActions>().SendInvitationAsync(req, ct),
        "API_IMPORTFROMCSV" => Get<QbXmlImportActions>().ImportFromCsvAsync(req, ct),
        "API_RUNIMPORT" => Get<QbXmlImportActions>().RunImportAsync(req, ct),
        "API_GENADDRECORDFORM" => Get<QbXmlImportActions>().GenAddRecordFormAsync(req, ct),
        _ => throw new QbXmlException(QbXmlErrors.NO_SUCH_OPERATION, "No Such Operation", "This operation does not exist. Please check the URL."),
    };
}

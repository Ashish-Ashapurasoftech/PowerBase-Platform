namespace PowerBase.API.QuickbaseApi.Common;

/// <summary>
/// Identifier rules for the Quickbase-compatible API (user decision 2026-11-04: use our existing Guids as-is).
/// App / table ids ("dbid"), report ids and user ids are our PublicId Guids in their standard string form;
/// field ids are the field's Fid (Quickbase numbering, 1–5 = system fields) and record ids are integers.
/// </summary>
public static class QbIds
{
    public const int RECORD_ID_FID = 3;
    public const int DATE_CREATED_FID = 1;
    public const int DATE_MODIFIED_FID = 2;
    public const int RECORD_OWNER_FID = 4;
    public const int LAST_MODIFIED_BY_FID = 5;

    public static string Format(Guid id) => id.ToString("D");

    /// <summary>Parses a required Guid identifier, or throws Quickbase's 400.</summary>
    public static Guid ParseGuid(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw QbApiException.BadRequest($"Required parameter '{parameterName}' not specified");
        if (!Guid.TryParse(value.Trim(), out var id))
            throw QbApiException.BadRequest($"Invalid value '{value}' for parameter '{parameterName}'");
        return id;
    }

    /// <summary>Parses a field id (fid) that Quickbase allows as integer or numeric string.</summary>
    public static int ParseFid(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || !int.TryParse(value.Trim(), out var fid) || fid <= 0)
            throw new QbApiException(400, "Bad Request", "Multiple errors found")
            {
                Details = new[] { $"'{parameterName}[0]' must be integer", $"'{parameterName}[0]' must match pattern \"^\\d+$\"",
                    $"'{parameterName}[0]' must match exactly one schema in oneOf" },
            };
        return fid;
    }
}

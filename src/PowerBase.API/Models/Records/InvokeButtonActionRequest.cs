namespace PowerBase.API.Models.Records;

/// <summary>Body for a POST .../actions/{fid} click. Only the fields relevant to the
/// button's configured variant need to be supplied; the server ignores the rest.</summary>
public class InvokeButtonActionRequest
{
    /// <summary>User-entered value for a Prompt button.</summary>
    public string? PromptValue { get; set; }

    /// <summary>Relative path returned by a prior file/signature upload (Signature/File buttons).</summary>
    public string? CapturedFileRef { get; set; }

    /// <summary>Password entered for a Password Gate, if the button has one configured.</summary>
    public string? Password { get; set; }

    public double? GeoLat { get; set; }
    public double? GeoLng { get; set; }
    public string? GeoState { get; set; }

    /// <summary>Informational only — the server clock is authoritative for Link Expiration.</summary>
    public DateTime? ClientNow { get; set; }
}

public class InvokeButtonActionResponse
{
    public Dictionary<string, object?> UpdatedFields { get; init; } = new();
    public string? Redirect { get; init; }
}

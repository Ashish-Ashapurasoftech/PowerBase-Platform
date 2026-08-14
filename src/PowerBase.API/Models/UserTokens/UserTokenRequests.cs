namespace PowerBase.API.Models.UserTokens;

public class CreateUserTokenRequest
{
    public string TokenName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool AccessAllApps { get; set; }
    public IEnumerable<Guid>? AllowedAppPublicIds { get; set; }
}

public class UpdateUserTokenRequest
{
    public string TokenName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool AccessAllApps { get; set; }
    public IEnumerable<Guid>? AllowedAppPublicIds { get; set; }
}

public class UpdateUserTokenStatusRequest
{
    public IEnumerable<Guid> PublicIds { get; set; } = Enumerable.Empty<Guid>();
    public bool IsActive { get; set; }
}

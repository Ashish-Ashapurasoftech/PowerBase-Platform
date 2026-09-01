namespace PowerBase.Application.Common.Models;

/// <summary>Slim projection of <see cref="PowerBase.Domain.Entities.AppTable"/> for resolving
/// <c>[_DBID_*]</c> formula tokens — just the two columns
/// <see cref="PowerBase.Application.Formulas.AppTableAliasSchema"/> actually needs. Unlike the
/// general-purpose <c>ListByAppAsync</c> (which joins in every field of every table in the app),
/// this carries no field data at all, so it stays cheap regardless of how many fields those
/// tables have.</summary>
public class AppTableAliasDto
{
    public Guid PublicId { get; set; }
    public string Alias { get; set; } = string.Empty;
}

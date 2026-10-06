using System.Net.Mail;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Imports;

/// <summary>The people told when an import finishes, besides the person who started it (who is always told).</summary>
public static class ImportNotify
{
    public const int MaxRecipients = 20;

    /// <summary>Trims, lower-cases and de-duplicates the addresses, and refuses anything that is not a plain address. The list is
    /// saved with the import and read again by the worker, so it is cleaned once, here.</summary>
    public static List<string> Normalize(IEnumerable<string>? emails)
    {
        var result = new List<string>();
        foreach (var raw in emails ?? [])
        {
            var email = raw?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(email)) continue;
            // A plain address only: not "Name <a@b.c>", which the mail library would also accept.
            if (email.Length > 254 || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || !parsed.Host.Contains('.'))
                throw new ValidationException(new Dictionary<string, string[]> { ["Import"] = [$"'{raw!.Trim()}' is not a valid email address."] });
            if (!result.Contains(email)) result.Add(email);
        }
        if (result.Count > MaxRecipients)
            throw new ValidationException(new Dictionary<string, string[]> { ["Import"] = [$"Notify at most {MaxRecipients} people besides yourself."] });
        return result;
    }
}

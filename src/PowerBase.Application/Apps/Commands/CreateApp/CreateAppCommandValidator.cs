using FluentValidation;

namespace PowerBase.Application.Apps.Commands.CreateApp;

public class CreateAppCommandValidator : AbstractValidator<CreateAppCommand>
{
    public CreateAppCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(500).When(x => x.Description is not null);
        RuleFor(x => x.Icon).MaximumLength(100).When(x => x.Icon is not null);
        RuleFor(x => x.Color).MaximumLength(20).When(x => x.Color is not null);

        // Duplicate table names (case-insensitive, trimmed — same comparison the DB's own
        // unique-name lookup effectively applies) must be caught here, before any table is
        // seeded. Without this, CreateAppCommandHandler seeds tables one at a time AFTER the
        // app itself is already committed, so a duplicate later in the list left every table
        // before it already created — a partially-built app. See SeedTableAsync.
        RuleFor(x => x.Tables)
            .Must(tables => FindDuplicateNames(tables!.Select(t => t.Name)).Count == 0)
            .WithMessage("Table name already exists. Duplicate table names are not allowed.")
            .When(x => x.Tables is { Count: > 1 });

        RuleForEach(x => x.Tables).ChildRules(tables =>
        {
            tables.RuleFor(t => t.Name).NotEmpty().MaximumLength(200);
            tables.RuleFor(t => t.SingularLabel).MaximumLength(200).When(t => t.SingularLabel is not null);
            tables.RuleFor(t => t.PluralLabel).MaximumLength(200).When(t => t.PluralLabel is not null);
            tables.RuleFor(t => t.Description).MaximumLength(500).When(t => t.Description is not null);
            tables.RuleFor(t => t.Icon).MaximumLength(100).When(t => t.Icon is not null);

            // Duplicate field names within THIS table only — the same field name is fine across
            // different tables, so this must stay scoped inside the per-table ChildRules rather
            // than flattened across x.Tables.
            tables.RuleFor(t => t.Fields)
                .Must(fields => FindDuplicateNames(fields!.Select(f => f.Label)).Count == 0)
                .WithMessage("Field name already exists in this table. Duplicate field names are not allowed.")
                .When(t => t.Fields is { Count: > 1 });

            tables.RuleForEach(t => t.Fields).ChildRules(fields =>
            {
                fields.RuleFor(f => f.Label).NotEmpty().MaximumLength(200);
                fields.RuleFor(f => f.TypeCode).NotEmpty();
            });
        });
    }

    /// <summary>Names that occur more than once, compared case-insensitively with surrounding
    /// whitespace trimmed. Blank names are ignored here — NotEmpty already reports those.</summary>
    private static List<string> FindDuplicateNames(IEnumerable<string> names)
    {
        return names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
    }
}

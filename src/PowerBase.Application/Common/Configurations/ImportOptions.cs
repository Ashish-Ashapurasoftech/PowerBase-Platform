namespace PowerBase.Application.Common.Configurations;

/// <summary>Settings of the Imports feature, from the "Imports" section of the configuration. Every value has a default, so the section is optional.</summary>
public class ImportOptions
{
    public const string SectionName = "Imports";
    public const int DefaultRetentionDays = 30;
    public const int MaxRetentionDays = 365;

    /// <summary>How long a run's details files and the file that was uploaded for it are kept, counted from when the run ended. After that they
    /// are deleted (the run's counts stay in the history).</summary>
    public int RetentionDays { get; set; } = DefaultRetentionDays;

    /// <summary>The retention in force: a value outside 1 to <see cref="MaxRetentionDays"/> (or missing) never means "keep nothing" or "keep forever".</summary>
    public int EffectiveRetentionDays => RetentionDays is >= 1 and <= MaxRetentionDays ? RetentionDays : DefaultRetentionDays;
}

using System;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;

namespace PowerBase.Application.Common.Models;

public class PauseStepConfig
{
    public const int MaximumSeconds = 30 * 60;

    [JsonPropertyName("duration")]
    public decimal? Duration { get; set; }

    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    [JsonPropertyName("durationText")]
    public string? DurationText { get; set; }

    public static TimeSpan ParseDuration(string? json)
    {
        PauseStepConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<PauseStepConfig>(json ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Pause configuration must be valid JSON.", nameof(json), ex);
        }

        decimal seconds;
        if (!string.IsNullOrWhiteSpace(config?.DurationText))
        {
            seconds = ParseDurationText(config.DurationText);
        }
        else
        {
            if (config?.Duration is not > 0)
                throw new ArgumentException("Pause duration must be greater than zero.", nameof(json));
            if (config.Duration > MaximumSeconds)
                throw new ArgumentException("Pause duration cannot exceed 30 minutes.", nameof(json));
            seconds = config.Unit?.Trim().ToLowerInvariant() switch
            {
                null or "second" or "seconds" => config.Duration.Value,
                "minute" or "minutes" => config.Duration.Value * 60,
                "hour" or "hours" => config.Duration.Value * 3600,
                "day" or "days" => config.Duration.Value * 86400,
                _ => throw new ArgumentException("Pause unit must be seconds, minutes, hours, or days.", nameof(json))
            };
        }
        if (seconds > MaximumSeconds)
            throw new ArgumentException("Pause duration cannot exceed 30 minutes.", nameof(json));

        return TimeSpan.FromSeconds((double)seconds);
    }

    private static decimal ParseDurationText(string text)
    {
        text = text.Trim();
        var clock = Regex.Match(text, @"^(?<minutes>\d+):(?<seconds>\d{1,2})$");
        if (clock.Success)
        {
            if (decimal.TryParse(clock.Groups["minutes"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) &&
                decimal.TryParse(clock.Groups["seconds"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
                minutes <= 30 && seconds < 60 && minutes * 60 + seconds > 0)
                return minutes * 60 + seconds;
        }
        else if (decimal.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var plain))
        {
            if (plain > 0) return plain;
        }
        else
        {
            var normalized = Regex.Replace(text.Replace(',', ' '), @"\s+", " ");
            var tokens = Regex.Matches(normalized, @"\b(?<value>\d+)\s*(?<unit>minutes?|mins?|m|seconds?|secs?|sec|s)\b", RegexOptions.IgnoreCase);
            decimal minutes = 0, seconds = 0;
            int minuteParts = 0, secondParts = 0;
            foreach (Match token in tokens)
            {
                if (!decimal.TryParse(token.Groups["value"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
                    break;
                if (amount > MaximumSeconds)
                    throw new ArgumentException("Pause duration cannot exceed 30 minutes.", nameof(text));
                if (token.Groups["unit"].Value.StartsWith("m", StringComparison.OrdinalIgnoreCase))
                {
                    minutes += amount;
                    minuteParts++;
                }
                else
                {
                    seconds += amount;
                    secondParts++;
                }
            }
            if (tokens.Count > 0 && minuteParts <= 1 && secondParts <= 1 &&
                !(minuteParts > 0 && secondParts > 0 && seconds >= 60) &&
                string.IsNullOrWhiteSpace(Regex.Replace(normalized, @"\b\d+\s*(?:minutes?|mins?|m|seconds?|secs?|sec|s)\b", "", RegexOptions.IgnoreCase)) &&
                minutes * 60 + seconds > 0)
                return minutes * 60 + seconds;
        }
        throw new ArgumentException("Pause duration must use seconds, minutes, a combination, or M:SS.", nameof(text));
    }
}

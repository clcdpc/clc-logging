using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NLog.Config;
using NLog.Layouts;

namespace Clc.Logging;

/// <summary>Startup settings. Configuration is read once when logging is registered.</summary>
public sealed class ClcLoggingOptions
{
    public ClcFileLoggingOptions File { get; } = new();
    public TimeSpan FlushTimeout { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Configuration levels, with callback additions/changes applied as native Microsoft filter rules.</summary>
    public IDictionary<string, LogLevel> LogLevels { get; } = new Dictionary<string, LogLevel>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Native layout shared by the conventional file and stderr targets. Null selects plaintext.</summary>
    public Layout? Layout { get; set; }

    /// <summary>Customizes the assembled host configuration before activation; never called during bootstrap.</summary>
    public Action<LoggingConfiguration>? ConfigureNLog { get; set; }

    private KeyValuePair<string, LogLevel>[] _filterOverrides = [];

    internal static ClcLoggingOptions Read(IConfiguration? configuration, Action<ClcLoggingOptions>? configure)
    {
        var options = new ClcLoggingOptions();
        if (configuration is not null) { options.ReadConfiguration(configuration); }
        options.Validate();
        var configuredLevels = new Dictionary<string, LogLevel>(options.LogLevels, StringComparer.OrdinalIgnoreCase);
        configure?.Invoke(options);
        options.Validate();
        options._filterOverrides = options.LogLevels
            .Where(pair => !configuredLevels.TryGetValue(pair.Key, out var level) || level != pair.Value).ToArray();
        return options;
    }

    private void ReadConfiguration(IConfiguration configuration)
    {
        var file = configuration.GetSection("Logging:File");
        RequireObject(file);
        foreach (var setting in file.GetChildren())
        {
            RequireScalar(setting);
            switch (setting.Key.ToUpperInvariant())
            {
                case "ENABLED":
                    if (!bool.TryParse(setting.Value, out var enabled)) { throw Invalid(setting.Path, "must be true or false"); }
                    File.Enabled = enabled;
                    break;
                case "MAXFILESIZEBYTES": File.MaxFileSizeBytes = ReadInteger(setting); break;
                case "RETENTIONDAYS": File.RetentionDays = ReadInt32(setting); break;
                case "MAXARCHIVEFILES": File.MaxArchiveFiles = ReadInt32(setting); break;
                default: throw Invalid(setting.Path, "is not a supported file setting");
            }
        }

        var levels = configuration.GetSection("Logging:LogLevel");
        RequireObject(levels);
        foreach (var setting in levels.GetChildren())
        {
            RequireScalar(setting);
            if (!Enum.TryParse<LogLevel>(setting.Value, true, out var level) ||
                !Enum.IsDefined(level) || !Enum.GetNames<LogLevel>().Contains(setting.Value, StringComparer.OrdinalIgnoreCase))
            {
                throw Invalid(setting.Path, "must be Trace, Debug, Information, Warning, Error, Critical or None");
            }
            LogLevels[setting.Key] = level;
        }
    }

    private void Validate()
    {
        if (File.MaxFileSizeBytes is < 1024 or > 1024L * 1024 * 1024) { throw Invalid("Logging:File:MaxFileSizeBytes", "must be between 1024 and 1073741824 bytes"); }
        if (File.RetentionDays is < 1 or > 3650) { throw Invalid("Logging:File:RetentionDays", "must be between 1 and 3650"); }
        if (File.MaxArchiveFiles is < 1 or > 10000) { throw Invalid("Logging:File:MaxArchiveFiles", "must be between 1 and 10000"); }
        if (FlushTimeout <= TimeSpan.Zero || FlushTimeout > TimeSpan.FromMinutes(1)) { throw Invalid(nameof(FlushTimeout), "must be greater than zero and at most one minute"); }
        foreach (var (category, level) in LogLevels)
        {
            if (string.IsNullOrWhiteSpace(category) || !Enum.IsDefined(level)) { throw Invalid("Logging:LogLevel", "requires category names and defined log levels"); }
        }
    }

    internal void ApplyOverrides(LoggerFilterOptions filters)
    {
        foreach (var (category, level) in _filterOverrides)
        {
            filters.Rules.Add(new LoggerFilterRule(null, category.Equals("Default", StringComparison.OrdinalIgnoreCase) ? null : category, level, null));
        }
    }

    internal static void ApplyDefaults(LoggerFilterOptions filters)
    {
        // Microsoft already defaults MinLevel to Information. Respect an application's
        // different minimum, and only fill category gaps left by its general rules.
        if (filters.MinLevel != LogLevel.Information || filters.Rules.Any(rule =>
            rule.ProviderName is null && rule.CategoryName?.Contains('*') == true)) { return; }
        var applicationRules = filters.Rules.ToArray();
        foreach (var (category, level) in new[]
        {
            ("Microsoft", LogLevel.Warning),
            ("Microsoft.Hosting.Lifetime", LogLevel.Information),
            ("System.Net.Http", LogLevel.Warning)
        })
        {
            if (applicationRules.Any(rule => rule.ProviderName is null &&
                (rule.CategoryName is null || category.StartsWith(rule.CategoryName, StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }
            // Provider-specific and more-specific application rules retain native precedence.
            filters.Rules.Insert(0, new LoggerFilterRule(null, category, level, null));
        }
    }

    private static void RequireObject(IConfigurationSection section)
    {
        if (section.Value is not null) { throw Invalid(section.Path, "must be an object"); }
    }

    private static void RequireScalar(IConfigurationSection section)
    {
        if (string.IsNullOrWhiteSpace(section.Value) || section.GetChildren().Any()) { throw Invalid(section.Path, "must be a non-null scalar value"); }
    }

    private static long ReadInteger(IConfigurationSection section) =>
        long.TryParse(section.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : throw Invalid(section.Path, "must be an integer");

    private static int ReadInt32(IConfigurationSection section)
    {
        var value = ReadInteger(section);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value : throw Invalid(section.Path, "is out of range");
    }

    private static InvalidOperationException Invalid(string path, string reason) => new($"{path} {reason}.");
}

public sealed class ClcFileLoggingOptions
{
    public bool Enabled { get; set; } = true;
    public long MaxFileSizeBytes { get; set; } = 50 * 1024 * 1024;
    public int RetentionDays { get; set; } = 30;
    public int MaxArchiveFiles { get; set; } = 300;
}

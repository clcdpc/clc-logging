using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Clc.Logging;

/// <summary>Startup settings. Configuration is read once when logging is registered.</summary>
public sealed class ClcLoggingOptions
{
    public ClcFileLoggingOptions File { get; } = new();
    public TimeSpan FlushTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public IDictionary<string, LogLevel> LogLevels { get; } = new Dictionary<string, LogLevel>(StringComparer.OrdinalIgnoreCase)
    {
        ["Default"] = LogLevel.Information,
        ["Microsoft"] = LogLevel.Warning,
        ["Microsoft.Hosting.Lifetime"] = LogLevel.Information,
        ["System.Net.Http"] = LogLevel.Warning
    };

    /// <summary>Optional application policy for event and scope property names.</summary>
    public Func<string, bool>? IncludeProperty { get; set; }

    /// <summary>Optional application transformation of string values, messages, templates and exceptions.</summary>
    public Func<string, string>? TransformText { get; set; }

    internal static ClcLoggingOptions Read(IConfiguration configuration, Action<ClcLoggingOptions>? configure)
    {
        var options = new ClcLoggingOptions();
        var file = configuration.GetSection("Logging:File");
        RequireObject(file);
        foreach (var setting in file.GetChildren())
        {
            RequireScalar(setting);
            switch (setting.Key.ToUpperInvariant())
            {
                case "ENABLED":
                    if (!bool.TryParse(setting.Value, out var enabled)) { throw Invalid(setting.Path, "must be true or false"); }
                    options.File.Enabled = enabled;
                    break;
                case "MAXFILESIZEBYTES": options.File.MaxFileSizeBytes = ReadInteger(setting); break;
                case "RETENTIONDAYS": options.File.RetentionDays = ReadInt32(setting); break;
                case "MAXARCHIVEFILES": options.File.MaxArchiveFiles = ReadInt32(setting); break;
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
            options.LogLevels[setting.Key] = level;
        }

        configure?.Invoke(options);
        if (options.File.MaxFileSizeBytes is < 1024 or > 1024L * 1024 * 1024) { throw Invalid("Logging:File:MaxFileSizeBytes", "must be between 1024 and 1073741824 bytes"); }
        if (options.File.RetentionDays is < 1 or > 3650) { throw Invalid("Logging:File:RetentionDays", "must be between 1 and 3650"); }
        if (options.File.MaxArchiveFiles is < 1 or > 10000) { throw Invalid("Logging:File:MaxArchiveFiles", "must be between 1 and 10000"); }
        if (options.FlushTimeout <= TimeSpan.Zero || options.FlushTimeout > TimeSpan.FromMinutes(1)) { throw Invalid(nameof(FlushTimeout), "must be greater than zero and at most one minute"); }
        foreach (var (category, level) in options.LogLevels)
        {
            if (string.IsNullOrWhiteSpace(category) || category.Contains('*') || !Enum.IsDefined(level)) { throw Invalid("Logging:LogLevel", "requires category prefixes and defined log levels"); }
        }
        return options;
    }

    internal void ApplyFilters(LoggerFilterOptions filters)
    {
        // Replace the framework's live configuration filters with this startup snapshot.
        // Consumers may append AddFilter calls after AddClcLogging.
        filters.MinLevel = LogLevel.Trace;
        filters.Rules.Clear();
        foreach (var (category, level) in LogLevels)
        {
            filters.Rules.Add(new LoggerFilterRule(null, category.Equals("Default", StringComparison.OrdinalIgnoreCase) ? null : category, level, null));
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

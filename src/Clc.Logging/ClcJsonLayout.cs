using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using NLog;
using NLog.Layouts;

namespace Clc.Logging;

// Synchronous rendering keeps ScopeContext and Activity on the originating thread.
internal sealed class ClcJsonLayout(ClcLoggingOptions options, Action<Exception>? exceptionRendered = null) : Layout
{
    protected override string GetFormattedMessage(LogEventInfo logEvent)
    {
        try
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var pair in logEvent.Properties)
            {
                var key = Convert.ToString(pair.Key, CultureInfo.InvariantCulture) ?? "";
                if (key is "EventId" or "EventName" or "{OriginalFormat}") { continue; }
                if (options.IncludeProperty?.Invoke(key) != false) { properties[key] = Value(pair.Value); }
            }
            var scope = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var pair in ScopeContext.GetAllProperties())
            {
                if (options.IncludeProperty?.Invoke(pair.Key) != false) { scope[pair.Key] = Value(pair.Value); }
            }
            var states = ScopeContext.GetAllNestedStates().Select(Value).ToArray();
            var activity = Activity.Current;
            var json = JsonSerializer.Serialize(new
            {
                timestamp = Timestamp(logEvent.TimeStamp),
                level = logEvent.Level.Name,
                category = Text(logEvent.LoggerName),
                eventId = logEvent.Properties.TryGetValue("EventId", out var id) ? id : 0,
                eventName = logEvent.Properties.TryGetValue("EventName", out var name) ? Value(name) : null,
                message = Text(logEvent.FormattedMessage),
                template = Text(logEvent.Message),
                properties,
                scope,
                scopes = states,
                traceId = activity?.TraceId.ToString(),
                spanId = activity?.SpanId.ToString(),
                exception = logEvent.Exception is { } error ? Text(error.ToString()) : null
            });
            if (logEvent.Exception is { } reported) { exceptionRendered?.Invoke(reported); }
            return json;
        }
        catch (Exception error)
        {
            // User formatters/ToString implementations must not cause silent event loss.
            return JsonSerializer.Serialize(new
            {
                timestamp = Timestamp(logEvent.TimeStamp), level = "Error", category = "Clc.Logging",
                message = "Log event formatting failed; inspect the application's logging values and callbacks.",
                formattingError = error.GetType().FullName
            });
        }
    }

    private string Text(string value) => options.TransformText?.Invoke(value) ?? value;

    // Never reflect over arbitrary application objects or walk request/configuration graphs.
    private object? Value(object? value) => value switch
    {
        null => null,
        bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal => value,
        float number when float.IsFinite(number) => number,
        double number when double.IsFinite(number) => number,
        _ => Text(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")
    };

    private static string Timestamp(DateTime time) => new DateTimeOffset(time.ToUniversalTime())
        .ToLocalTime().ToString("O", CultureInfo.InvariantCulture);
}

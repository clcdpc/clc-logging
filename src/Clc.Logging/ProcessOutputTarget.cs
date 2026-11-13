using NLog;
using NLog.Layouts;
using NLog.Targets;

namespace Clc.Logging;

internal sealed class ProcessOutputTarget(Layout layout, bool fileFailed = false) : TargetWithLayout
{
    protected override void InitializeTarget()
    {
        Layout = layout;
        base.InitializeTarget();
    }

    protected override void Write(LogEventInfo logEvent)
    {
        if (fileFailed)
        {
            Console.Error.WriteLine("Clc.Logging: file logging unavailable; check Logs permissions, file locks and disk space. Event follows on stderr.");
        }
        Console.Error.WriteLine(RenderLogEvent(Layout, logEvent));
    }
}

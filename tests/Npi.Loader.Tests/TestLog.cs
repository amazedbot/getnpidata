using System.Globalization;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Npi.Loader.Tests;

/// <summary>
/// A logger for the loader under test that writes its warnings and errors to the running test's output, which the test
/// report shows when the test fails. A load that returns false ("a source failed") then says why: with
/// <see cref="Logger.None"/> a failed <c>Assert.True(await loader.RefreshAsync(…))</c> showed only "Expected True, Actual
/// False" (a MySQL "Too many connections" while other suites ran, or a state left out).
/// </summary>
public static class TestLog
{
    public static ILogger Warnings { get; } = new LoggerConfiguration().MinimumLevel.Warning().WriteTo.Sink(new OutputSink()).CreateLogger();

    // The current test is found per call (TestContext.Current flows with the async calls of the test), so one logger serves all.
    private sealed class OutputSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            var output = TestContext.Current.TestOutputHelper;
            if (output is null)
            {
                return;
            }

            var line = $"{logEvent.Timestamp:HH:mm:ss.fff} {logEvent.Level} {logEvent.RenderMessage(CultureInfo.InvariantCulture)}";
            try
            {
                output.WriteLine(logEvent.Exception is null ? line : $"{line}{Environment.NewLine}{logEvent.Exception}");
            }
            catch (InvalidOperationException)
            {
                // the test has already finished (a background step logging late)
            }
        }
    }
}

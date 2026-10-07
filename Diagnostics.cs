using System.Text.Json;

namespace MpoFix;

internal interface IDiagnosticSink
{
    void Write(string category, string message);
}

internal sealed class DiagnosticException(string message, Exception inner) : Exception(message, inner);

internal sealed class FileDiagnosticSink(string path, IObservationClock clock, string runId) : IDiagnosticSink
{
    public void Write(string category, string message)
    {
        var line = JsonSerializer.Serialize(new { timestamp = clock.UtcNow, runId, category, message });
        try
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            throw new DiagnosticException($"Cannot persist diagnostics: {ex.GetType().Name}, HResult=0x{ex.HResult:X8}", ex);
        }
        Console.WriteLine(line);
    }
}

internal interface IObservationClock
{
    DateTimeOffset UtcNow { get; }
    void Wait(TimeSpan duration);
}

internal sealed class SystemObservationClock : IObservationClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public void Wait(TimeSpan duration) => Thread.Sleep(duration);
}

internal static class Diagnostics
{
    internal static IDiagnosticSink Sink { get; } = new FileDiagnosticSink(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mpofix.log"),
        new SystemObservationClock(), Guid.NewGuid().ToString("N"));

    internal static string ExceptionDetail(Exception ex) =>
        $"{ex.GetType().FullName}, HResult=0x{ex.HResult:X8}: {ex.Message}";
}

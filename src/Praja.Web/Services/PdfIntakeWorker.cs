using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Praja.Web.Services;

// Parse untrusted PDFs in a separate process with an elapsed-time and memory watchdog.
// The worker consumes bytes over stdin; it never opens caller-supplied paths or runs embedded PDF actions.
public static class PdfIntakeWorker
{
    public static async Task RunAsync()
    {
        try
        {
            var encoded = await Console.In.ReadLineAsync() ?? "";
            if (encoded.Length > 12 * 1024 * 1024) throw new InvalidOperationException();
            var bytes = Convert.FromBase64String(encoded);
            using var document = PdfDocument.Open(bytes);
            if (document.NumberOfPages > 10) { Console.WriteLine(JsonSerializer.Serialize(new { error = "Use a petition of at most 10 pages." })); return; }
            var text = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                text.AppendLine(ContentOrderTextExtractor.GetText(page));
                if (text.Length > 6000) { Console.WriteLine(JsonSerializer.Serialize(new { error = "This petition exceeds 6,000 characters. Enter a reviewed excerpt manually." })); return; }
            }
            Console.WriteLine(JsonSerializer.Serialize(new { text = text.ToString() }));
        }
        catch (Exception) { Console.WriteLine(JsonSerializer.Serialize(new { error = "The PDF could not be read. Use an unencrypted text PDF or enter the complaint manually." })); }
    }

    public static async Task<string> ExtractAsync(byte[] bytes, CancellationToken cancellation)
    {
        var host = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(PdfIntakeWorker).Assembly.Location);
        start.ArgumentList.Add("--extract-pdf-worker");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PDF extraction could not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var output = ReadOutput(process.StandardOutput, deadline.Token);
            var errors = process.StandardError.ReadToEndAsync(deadline.Token);
            var monitoring = Monitor(process, deadline.Token);
            await process.StandardInput.WriteLineAsync(Convert.ToBase64String(bytes).AsMemory(), deadline.Token);
            process.StandardInput.Close();
            await monitoring;
            var json = await output; await errors;
            if (process.ExitCode != 0) throw new InvalidOperationException("PDF extraction failed. Try a smaller text PDF.");
            using var result = JsonDocument.Parse(json);
            if (result.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
            return result.RootElement.GetProperty("text").GetString() ?? "";
        }
        catch (Exception ex) when (ex is OperationCanceledException or JsonException or IOException)
        {
            cancellation.ThrowIfCancellationRequested();
            throw new InvalidOperationException("PDF extraction timed out or failed. Try a smaller text PDF or enter the complaint manually.");
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
    private static async Task Monitor(Process process, CancellationToken cancellation)
    {
        while (!process.HasExited)
        {
            process.Refresh();
            if (process.WorkingSet64 > 256 * 1024 * 1024) { process.Kill(entireProcessTree: true); throw new InvalidOperationException("PDF exceeds the extraction memory limit. Use a smaller document."); }
            await Task.Delay(100, cancellation);
        }
    }
    private static async Task<string> ReadOutput(StreamReader reader, CancellationToken cancellation)
    {
        var result = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation)) > 0)
        {
            if (result.Length + count > 100_000) throw new InvalidOperationException("PDF output exceeded the extraction limit.");
            result.Append(buffer,0,count);
        }
        return result.ToString();
    }
}

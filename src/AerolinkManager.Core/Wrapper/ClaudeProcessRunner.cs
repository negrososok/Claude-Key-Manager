using System.Diagnostics;
using System.Text;

namespace AerolinkManager.Core.Wrapper;

public sealed record ClaudeProcessResult(int ExitCode, string CapturedOutput);

public sealed class ClaudeProcessRunner
{
    private readonly bool? _redirectStandardInput;
    private readonly bool? _redirectStandardOutput;
    private readonly bool? _redirectStandardError;

    public ClaudeProcessRunner(
        bool? redirectStandardInput = null,
        bool? redirectStandardOutput = null,
        bool? redirectStandardError = null)
    {
        _redirectStandardInput = redirectStandardInput;
        _redirectStandardOutput = redirectStandardOutput;
        _redirectStandardError = redirectStandardError;
    }

    public async Task<ClaudeProcessResult> RunAsync(
        WrapperLaunchPlan plan,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken = default)
    {
        var redirectInput = _redirectStandardInput ?? Console.IsInputRedirected;
        var redirectOutput = _redirectStandardOutput ?? Console.IsOutputRedirected;
        var redirectError = _redirectStandardError ?? Console.IsErrorRedirected;

        var start = new ProcessStartInfo
        {
            FileName = plan.RealClaudePath,
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectError
        };
        foreach (var argument in plan.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var pair in environment)
        {
            start.Environment[pair.Key] = pair.Value;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Claude Code.");
        var captured = new StringBuilder();
        var input = redirectInput
            ? PumpInputAsync(process, cancellationToken)
            : Task.CompletedTask;
        var stdout = redirectOutput
            ? PumpOutputAsync(process.StandardOutput, Console.Out, captured, cancellationToken)
            : Task.CompletedTask;
        var stderr = redirectError
            ? PumpOutputAsync(process.StandardError, Console.Error, captured, cancellationToken)
            : Task.CompletedTask;

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        _ = input.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return new ClaudeProcessResult(process.ExitCode, captured.ToString());
    }

    private static async Task PumpInputAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await Console.OpenStandardInput().CopyToAsync(process.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private static async Task PumpOutputAsync(StreamReader reader, TextWriter writer, StringBuilder captured, CancellationToken cancellationToken)
    {
        var buffer = new char[512];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            var text = new string(buffer, 0, count);
            lock (captured)
            {
                captured.Append(text);
            }
            await writer.WriteAsync(text).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
    }
}

using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Octopus.Core.Agents;

namespace Octopus.Agents.Probes;

/// <summary>What one execution produced, before caching and back-off.</summary>
internal sealed record ProbeOutcome(ProbeErrorKind Error, string? Message, string? Json)
{
    public static ProbeOutcome Fail(ProbeErrorKind error, string message) => new(error, message, null);
}

/// <summary>
/// Starts the agent's executable for one probe and reads its answer. No shell is involved; the process tree is killed
/// when the answer is in, on timeout, or when the output passes the cap, so an RPC server that would keep running
/// (<c>codex app-server</c>) never outlives the probe.
/// </summary>
internal static class ProbeProcess
{
    /// <summary>How long to wait for a killed process to go away before giving up on it.</summary>
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(2);

    /// <summary>After an RPC server exits, how long its remaining output may take to arrive.</summary>
    private static readonly TimeSpan ExitDrain = TimeSpan.FromMilliseconds(300);

    public static async Task<ProbeOutcome> RunAsync(
        AgentProbe probe,
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string? workingDirectory,
        Func<string, string> fillPlaceholders,
        int maxOutputBytes,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment.Clear();
        foreach (var (key, value) in environment)
        {
            start.Environment[key] = value;
        }
        if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
        {
            start.WorkingDirectory = workingDirectory;
        }

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                return ProbeOutcome.Fail(ProbeErrorKind.StartFailed, "the program did not start");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            return ProbeOutcome.Fail(ProbeErrorKind.StartFailed, ex.Message);
        }

        // Standard error is read only so a chatty program cannot block on a full pipe; its content is not kept.
        var stderr = DrainAsync(process.StandardError.BaseStream);
        try
        {
            return string.Equals(probe.Kind, ProbeKinds.JsonRpcStdio, StringComparison.Ordinal)
                ? await RunJsonRpcAsync(probe, process, fillPlaceholders, maxOutputBytes, cancellationToken).ConfigureAwait(false)
                : await RunCommandAsync(probe, process, maxOutputBytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Kill(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(ExitGrace).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            try
            {
                await stderr.WaitAsync(ExitGrace).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
    }

    private static async Task<ProbeOutcome> RunCommandAsync(AgentProbe probe, Process process, int maxOutputBytes, CancellationToken token)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }
        var output = new MemoryStream();
        try
        {
            var chunk = new byte[64 * 1024];
            var stdout = process.StandardOutput.BaseStream;
            int n;
            while ((n = await stdout.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + n > maxOutputBytes)
                {
                    return ProbeOutcome.Fail(ProbeErrorKind.OutputTooLarge, $"output passed {maxOutputBytes} bytes");
                }
                output.Write(chunk, 0, n);
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ProbeOutcome.Fail(ProbeErrorKind.Timeout, "the program did not finish in time");
        }
        if (process.ExitCode != 0)
        {
            return ProbeOutcome.Fail(ProbeErrorKind.ExitCode, $"exit code {process.ExitCode}");
        }

        var bytes = output.GetBuffer().AsMemory(0, (int)output.Length);
        if (string.Equals(probe.Parse, "text", StringComparison.OrdinalIgnoreCase))
        {
            // Text has no members to select from; it is handed over as one JSON string.
            if (probe.Select.Count > 0)
            {
                return ProbeOutcome.Fail(ProbeErrorKind.InvalidOutput, "a text result has nothing to select");
            }
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStringValue(Encoding.UTF8.GetString(bytes.Span).TrimEnd('\n', '\r'));
            }
            return new ProbeOutcome(ProbeErrorKind.None, null, Encoding.UTF8.GetString(buffer.WrittenSpan));
        }
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return Select(probe, document.RootElement);
        }
        catch (JsonException)
        {
            return ProbeOutcome.Fail(ProbeErrorKind.InvalidOutput, "the output is not JSON");
        }
    }

    private static async Task<ProbeOutcome> RunJsonRpcAsync(
        AgentProbe probe,
        Process process,
        Func<string, string> fillPlaceholders,
        int maxOutputBytes,
        CancellationToken token)
    {
        if (probe.Requests.Count == 0)
        {
            return ProbeOutcome.Fail(ProbeErrorKind.NotDeclared, "the probe declares no requests");
        }
        var pending = new Dictionary<long, TaskCompletionSource<JsonElement>>();
        var gate = new object();
        var reading = ReadLinesAsync(process.StandardOutput.BaseStream, maxOutputBytes, line =>
        {
            JsonElement message;
            try
            {
                using var document = JsonDocument.Parse(line);
                message = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return; // log lines and other noise between messages
            }
            // A response carries our id and no method; requests and notifications from the server are ignored.
            if (message.ValueKind != JsonValueKind.Object
                || message.TryGetProperty("method", out _)
                || !message.TryGetProperty("id", out var idElement)
                || idElement.ValueKind != JsonValueKind.Number
                || !idElement.TryGetInt64(out var id))
            {
                return;
            }
            TaskCompletionSource<JsonElement>? waiter;
            lock (gate)
            {
                pending.Remove(id, out waiter);
            }
            waiter?.TrySetResult(message);
        }, token);
        var ended = false;
        void FailWaiters(Exception exception)
        {
            List<TaskCompletionSource<JsonElement>> waiters;
            lock (gate)
            {
                ended = true;
                waiters = [.. pending.Values];
                pending.Clear();
            }
            foreach (var waiter in waiters)
            {
                waiter.TrySetException(exception);
            }
        }
        // When the output ends (exit, overflow, cancellation), whoever still waits gets the reason.
        _ = reading.ContinueWith(t =>
        {
            _ = t.Exception; // observed: a read cut short by the timeout is expected
            FailWaiters(t.IsCompletedSuccessfully && t.Result ? new ProbeOutputTooLargeException() : new EndOfStreamException());
        }, TaskScheduler.Default);
        // A server that exits while a child it started still holds the output pipe would otherwise only be noticed at
        // the timeout. Its last lines get a moment to arrive first.
        _ = process.WaitForExitAsync(token).ContinueWith(async t =>
        {
            if (t.IsCompletedSuccessfully)
            {
                await Task.Delay(ExitDrain).ConfigureAwait(false);
                FailWaiters(new EndOfStreamException());
            }
        }, TaskScheduler.Default);

        var resultIndex = probe.Requests.FindIndex(r => r.Result);
        if (resultIndex < 0)
        {
            resultIndex = probe.Requests.Count - 1;
        }
        var input = process.StandardInput.BaseStream;
        try
        {
            for (var i = 0; i <= resultIndex; i++)
            {
                var request = probe.Requests[i];
                long id = i + 1;
                var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (gate)
                {
                    if (ended)
                    {
                        return reading.IsCompletedSuccessfully && reading.Result
                            ? ProbeOutcome.Fail(ProbeErrorKind.OutputTooLarge, $"output passed {maxOutputBytes} bytes")
                            : ProbeOutcome.Fail(ProbeErrorKind.ExitCode, "the program exited before answering");
                    }
                    pending[id] = waiter;
                }
                await input.WriteAsync(BuildRequest(id, request, fillPlaceholders), token).ConfigureAwait(false);
                await input.FlushAsync(token).ConfigureAwait(false);

                var response = await waiter.Task.WaitAsync(token).ConfigureAwait(false);
                if (response.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                {
                    return ProbeOutcome.Fail(ProbeErrorKind.RpcError, $"{request.Method}: {ErrorMessage(error)}");
                }
                if (i == resultIndex)
                {
                    return response.TryGetProperty("result", out var result)
                        ? Select(probe, result)
                        : ProbeOutcome.Fail(ProbeErrorKind.InvalidOutput, $"{request.Method}: the response has no result");
                }
            }
        }
        catch (OperationCanceledException)
        {
            return ProbeOutcome.Fail(ProbeErrorKind.Timeout, "no answer in time");
        }
        catch (ProbeOutputTooLargeException)
        {
            return ProbeOutcome.Fail(ProbeErrorKind.OutputTooLarge, $"output passed {maxOutputBytes} bytes");
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ObjectDisposedException)
        {
            return ProbeOutcome.Fail(ProbeErrorKind.ExitCode, "the program exited before answering");
        }
        return ProbeOutcome.Fail(ProbeErrorKind.InvalidOutput, "no result");
    }

    /// <summary>One newline-terminated JSON-RPC 2.0 request; <c>params</c> is left out when the definition has none.</summary>
    internal static byte[] BuildRequest(long id, ProbeRpcRequest request, Func<string, string> fillPlaceholders)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", id);
            writer.WriteString("method", request.Method);
            if (request.Params is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } parameters)
            {
                writer.WritePropertyName("params");
                WriteFilled(writer, parameters, fillPlaceholders);
            }
            writer.WriteEndObject();
        }
        buffer.Write("\n"u8);
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteFilled(Utf8JsonWriter writer, JsonElement element, Func<string, string> fill)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteFilled(writer, property.Value, fill);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteFilled(writer, item, fill);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(fill(element.GetString()!));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string ErrorMessage(JsonElement error)
    {
        var text = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
            ? message.GetString()!
            : "error";
        return text.Length > 200 ? text[..200] : text;
    }

    private static ProbeOutcome Select(AgentProbe probe, JsonElement result) =>
        JsonSelect.Apply(result, probe.Select) is { } json
            ? new ProbeOutcome(ProbeErrorKind.None, null, json)
            : ProbeOutcome.Fail(ProbeErrorKind.InvalidOutput, "the result is not an object");

    /// <summary>
    /// Splits standard output into lines and counts every byte against the cap. Returns true when the cap was passed
    /// (reading stops there), false at the end of the output.
    /// </summary>
    private static async Task<bool> ReadLinesAsync(Stream stream, int cap, Action<ReadOnlyMemory<byte>> onLine, CancellationToken token)
    {
        var chunk = new byte[64 * 1024];
        var line = new MemoryStream();
        long total = 0;
        int n;
        while ((n = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            total += n;
            if (total > cap)
            {
                return true;
            }
            var startOfLine = 0;
            for (var i = 0; i < n; i++)
            {
                if (chunk[i] != (byte)'\n')
                {
                    continue;
                }
                line.Write(chunk, startOfLine, i - startOfLine);
                onLine(line.GetBuffer().AsMemory(0, (int)line.Length));
                line.SetLength(0);
                startOfLine = i + 1;
            }
            line.Write(chunk, startOfLine, n - startOfLine);
        }
        if (line.Length > 0)
        {
            onLine(line.GetBuffer().AsMemory(0, (int)line.Length));
        }
        return false;
    }

    private static async Task DrainAsync(Stream stream)
    {
        var chunk = new byte[16 * 1024];
        try
        {
            while (await stream.ReadAsync(chunk).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    private sealed class ProbeOutputTooLargeException : Exception;
}

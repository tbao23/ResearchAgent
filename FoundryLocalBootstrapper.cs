using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DocumentChatBot;

public static class FoundryLocalBootstrapper
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Ensures the service is running and the configured model is downloaded and loaded.
    /// Returns the resolved model id (e.g. "Phi-4-mini-instruct-generic-cpu:5") to use with
    /// the OpenAI-compatible endpoint — which, unlike the `foundry` CLI, does not accept the
    /// friendly alias (e.g. "phi-4-mini") — or null if Foundry Local could not be readied.
    /// </summary>
    public static async Task<string?> EnsureReadyAsync(AiSettings ai, CancellationToken ct)
    {
        Console.WriteLine("Checking connection to Foundry Local...");

        if (await IsReachableAsync(ai, ct))
        {
            Console.WriteLine("Foundry Local service is running.");
        }
        else
        {
            Console.WriteLine("Foundry Local service is not reachable. Attempting to start it...");
            if (!await RunFoundryCommandAsync("service start", ct))
                return null;
        }

        // Both commands are idempotent — they return quickly if the model is already
        // downloaded/loaded, and do the (possibly slow) work only the first time.
        Console.WriteLine($"Ensuring model '{ai.Model}' is downloaded (this may take a while on first run)...");
        if (!await RunFoundryCommandAsync($"model download {ai.Model}", ct, TimeSpan.FromMinutes(30)))
            return null;

        Console.WriteLine($"Ensuring model '{ai.Model}' is loaded...");
        if (!await RunFoundryCommandAsync($"model load {ai.Model} --ttl {ai.LoadTimeToLiveSeconds}", ct))
            return null;

        if (!await IsReachableAsync(ai, ct))
        {
            Console.Error.WriteLine("Foundry Local still isn't reachable after attempting to start it.");
            return null;
        }

        var modelId = await ResolveModelIdAsync(ai.Model, ct);
        if (modelId is null)
        {
            Console.Error.WriteLine($"Could not resolve alias '{ai.Model}' to a model id via 'foundry model info'.");
            return null;
        }

        Console.WriteLine($"Foundry Local is ready (model id: {modelId}).");
        return modelId;
    }

    /// <summary>
    /// The OpenAI-compatible endpoint requires the fully resolved model id, not the alias
    /// used by the `foundry` CLI. "foundry model info &lt;alias&gt;" prints a table whose last
    /// column is that id (always of the form "name:version"), which we parse out here.
    /// </summary>
    private static async Task<string?> ResolveModelIdAsync(string alias, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("foundry", $"model info {alias}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return null;
        }

        string stdout = await process.StandardOutput.ReadToEndAsync(ct);
        await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            return null;

        foreach (var line in stdout.Split('\n'))
        {
            var match = Regex.Match(line.TrimEnd(), @"(\S+:\d+)\s*$");
            if (match.Success)
                return match.Groups[1].Value;
        }

        return null;
    }

    private static async Task<bool> IsReachableAsync(AiSettings ai, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await http.GetAsync($"{ai.Endpoint.TrimEnd('/')}/models", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> RunFoundryCommandAsync(string arguments, CancellationToken ct, TimeSpan? timeout = null)
    {
        Console.WriteLine($"  Running: foundry {arguments}");

        // Streams are left inheriting this console (not redirected) so progress bars
        // (e.g. model download %) are visible live, and so the child can never block
        // trying to write to an output pipe nobody is draining.
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("foundry", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = false,
            }
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            Console.Error.WriteLine($"  Could not run the 'foundry' CLI: {ex.Message}");
            Console.Error.WriteLine("  Make sure Foundry Local is installed and 'foundry' is on your PATH.");
            return false;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? CommandTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            Console.Error.WriteLine($"  Timed out waiting for 'foundry {arguments}' to finish.");
            return false;
        }

        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine($"  'foundry {arguments}' failed (exit code {process.ExitCode}).");
            return false;
        }

        return true;
    }
}

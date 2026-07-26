using DocumentChatBot;
using Microsoft.Extensions.Configuration;

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------
IConfiguration configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

// Pass a custom corpus path as a command line argument if needed:
//   dotnet run -- "C:\path\to\your\documents"
string? corpusDirOverride = args.Length > 0 && Directory.Exists(args[0]) ? args[0] : null;

// ---------------------------------------------------------------------------
// Cancellation
// ---------------------------------------------------------------------------
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

// ---------------------------------------------------------------------------
// Build the corpus index and connect to Foundry Local
// ---------------------------------------------------------------------------
var engine = await CorpusChatEngine.CreateFromConfigurationAsync(configuration, cancellation.Token, corpusDirOverride);
if (engine is null)
    return; // CreateFromConfigurationAsync already logged the specific reason

// ---------------------------------------------------------------------------
// Chat loop
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("Regulatory Document Assistant");
Console.WriteLine($"Corpus: {engine.ChunkCount} chunks indexed from {engine.CorpusDirectory}");
Console.WriteLine("Type your question and press Enter. Type 'exit' to quit.");
Console.WriteLine(new string('-', 60));

while (!cancellation.IsCancellationRequested)
{
    Console.WriteLine();
    Console.Write("You: ");
    string? userInput = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(userInput))
        continue;

    if (userInput.Trim().ToLower() is "exit" or "quit" or "bye")
        break;

    var turn = engine.Ask(userInput, cancellation.Token);

    Console.WriteLine();
    Console.Write("Assistant: ");

    try
    {
        await foreach (string text in turn.TextStream)
            Console.Write(text);

        Console.WriteLine();

        if (turn.Sources.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Sources:");
            foreach (var source in turn.Sources)
                Console.WriteLine($"  - {source}");
        }
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        Console.WriteLine("\n[Canceled]");
        break;
    }
    catch (HttpRequestException ex)
    {
        Console.Error.WriteLine($"\n[Could not reach Foundry Local: {ex.Message}]");
        Console.Error.WriteLine("Make sure 'foundry service start' and 'foundry model load phi-4-mini' have been run.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"\n[Unexpected error: {ex.Message}]");
    }
}

Console.WriteLine("Goodbye.");

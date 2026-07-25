using System.ClientModel;
using DocumentChatBot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------
IConfiguration configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var ai = configuration.GetSection("Ai").Get<AiSettings>() ?? new AiSettings();
var corpusSettings = configuration.GetSection("Corpus").Get<CorpusSettings>() ?? new CorpusSettings();

string corpusDir = Path.Combine(AppContext.BaseDirectory, corpusSettings.Directory);

// Pass a custom corpus path as a command line argument if needed:
//   dotnet run -- "C:\path\to\your\documents"
if (args.Length > 0 && Directory.Exists(args[0]))
    corpusDir = args[0];

// ---------------------------------------------------------------------------
// Build corpus index
// ---------------------------------------------------------------------------
if (!Directory.Exists(corpusDir))
{
    Console.Error.WriteLine($"Corpus directory not found: {corpusDir}");
    Console.Error.WriteLine("Create a 'data/corpus' folder next to the executable and add your documents.");
    return;
}

var chunks = DocumentLoader.LoadCorpus(corpusDir);
if (chunks.Count == 0)
{
    Console.Error.WriteLine("No documents loaded. Add .txt, .pdf, or .docx files to the corpus directory.");
    return;
}

var index = new TextIndex();
index.Build(chunks);

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
// Ensure Foundry Local is running, then build the MAF agent
// ---------------------------------------------------------------------------
string? modelId = await FoundryLocalBootstrapper.EnsureReadyAsync(ai, cancellation.Token);
if (modelId is null)
{
    Console.Error.WriteLine("Could not connect to Foundry Local. Fix the error above and try again.");
    return;
}

var openAIClient = new OpenAIClient(
    new ApiKeyCredential(ai.ApiKey),
    new OpenAIClientOptions { Endpoint = new Uri(ai.Endpoint) });

AIAgent agent = openAIClient
    .GetChatClient(modelId)
    .AsIChatClient()
    .AsAIAgent(
        name: ai.AgentName,
        instructions: ai.Instructions);

// ---------------------------------------------------------------------------
// Chat loop
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("Regulatory Document Assistant");
Console.WriteLine($"Corpus: {index.ChunkCount} chunks indexed from {corpusDir}");
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

    // Search corpus
    var results = index.Search(userInput, topK: 5);
    string context = index.FormatResults(results);
    var sources = results.Select(r => r.Chunk.FileName).Distinct().ToList();

    // Build grounded prompt
    string prompt = results.Count == 0
        ? $"Question: {userInput}\n\n" +
          "(No relevant documents were found in the corpus for this question.)"
        : $"""
          The following passages were retrieved from the regulatory document corpus:

          {context}

          ---

          Based only on the above documents, answer this question:
          {userInput}
          """;

    // Stream the response
    Console.WriteLine();
    Console.Write("Assistant: ");

    try
    {
        await foreach (AgentResponseUpdate update in
            agent.RunStreamingAsync(prompt, cancellationToken: cancellation.Token))
        {
            if (!string.IsNullOrEmpty(update.Text))
                Console.Write(update.Text);
        }

        Console.WriteLine();

        if (sources.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Sources:");
            foreach (var source in sources)
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

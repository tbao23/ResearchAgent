using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;

namespace DocumentChatBot;

public record DocumentSummary(string FileName, string Date, bool Superseded);

public record ChatTurn(IAsyncEnumerable<string> TextStream, IReadOnlyList<string> Sources);

/// <summary>
/// Loads the corpus, builds the search index, and wraps the Foundry Local agent
/// so every host (console, web) shares the same retrieval + prompting logic.
/// </summary>
public class CorpusChatEngine
{
    private readonly TextIndex _index;
    private readonly AIAgent _agent;

    public IReadOnlyList<DocumentSummary> Documents { get; }
    public string CorpusDirectory { get; }
    public int ChunkCount => _index.ChunkCount;

    private CorpusChatEngine(TextIndex index, AIAgent agent, IReadOnlyList<DocumentSummary> documents, string corpusDir)
    {
        _index = index;
        _agent = agent;
        Documents = documents;
        CorpusDirectory = corpusDir;
    }

    /// <summary>
    /// Binds AiSettings/CorpusSettings from configuration and resolves the corpus directory
    /// before delegating to <see cref="CreateAsync"/>, so every host shares one bootstrap path.
    /// </summary>
    public static Task<CorpusChatEngine?> CreateFromConfigurationAsync(
        IConfiguration configuration, CancellationToken ct, string? corpusDirOverride = null)
    {
        var ai = configuration.GetSection("Ai").Get<AiSettings>() ?? new AiSettings();
        var corpusSettings = configuration.GetSection("Corpus").Get<CorpusSettings>() ?? new CorpusSettings();
        string corpusDir = corpusDirOverride ?? Path.Combine(AppContext.BaseDirectory, corpusSettings.Directory);

        return CreateAsync(ai, corpusDir, ct);
    }

    public static async Task<CorpusChatEngine?> CreateAsync(AiSettings ai, string corpusDir, CancellationToken ct)
    {
        if (!Directory.Exists(corpusDir))
        {
            Console.Error.WriteLine($"Corpus directory not found: {corpusDir}");
            return null;
        }

        var chunks = DocumentLoader.LoadCorpus(corpusDir);
        if (chunks.Count == 0)
        {
            Console.Error.WriteLine("No documents loaded. Add .txt, .pdf, or .docx files to the corpus directory.");
            return null;
        }

        var documents = chunks
            .GroupBy(c => c.FileName)
            .Select(g => new DocumentSummary(g.Key, g.First().Date, g.First().Superseded))
            .OrderBy(d => d.FileName)
            .ToList();

        var index = new TextIndex();
        index.Build(chunks);

        string? modelId = await FoundryLocalBootstrapper.EnsureReadyAsync(ai, ct);
        if (modelId is null)
        {
            Console.Error.WriteLine("Could not connect to Foundry Local. Fix the error above and try again.");
            return null;
        }

        var openAIClient = new OpenAIClient(
            new ApiKeyCredential(ai.ApiKey),
            new OpenAIClientOptions { Endpoint = new Uri(ai.Endpoint) });

        AIAgent agent = openAIClient
            .GetChatClient(modelId)
            .AsIChatClient()
            .AsAIAgent(name: ai.AgentName, instructions: ai.Instructions);

        return new CorpusChatEngine(index, agent, documents, corpusDir);
    }

    public ChatTurn Ask(string question, CancellationToken ct)
    {
        var results = _index.Search(question, topK: 5);
        string context = _index.FormatResults(results);
        var sources = results.Select(r => r.Chunk.FileName).Distinct().ToList();

        string prompt = results.Count == 0
            ? $"Question: {question}\n\n" +
              "(No relevant documents were found in the corpus for this question.)"
            : $"""
              The following passages were retrieved from the regulatory document corpus:

              {context}

              ---

              Based only on the above documents, answer this question:
              {question}
              """;

        return new ChatTurn(StreamResponse(prompt, ct), sources);
    }

    private async IAsyncEnumerable<string> StreamResponse(
        string prompt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (AgentResponseUpdate update in _agent.RunStreamingAsync(prompt, cancellationToken: ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
                yield return update.Text;
        }
    }
}

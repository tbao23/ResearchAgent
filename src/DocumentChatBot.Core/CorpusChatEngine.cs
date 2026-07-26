using System.ClientModel;
using System.Text;
using System.Text.RegularExpressions;
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
    private readonly ChatClientAgentRunOptions _runOptions;

    public IReadOnlyList<DocumentSummary> Documents { get; }
    public string CorpusDirectory { get; }
    public int ChunkCount => _index.ChunkCount;

    private CorpusChatEngine(TextIndex index, AIAgent agent, IReadOnlyList<DocumentSummary> documents, string corpusDir, int maxOutputTokens)
    {
        _index = index;
        _agent = agent;
        Documents = documents;
        CorpusDirectory = corpusDir;
        _runOptions = new ChatClientAgentRunOptions(new ChatOptions { MaxOutputTokens = maxOutputTokens });
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

        return new CorpusChatEngine(index, agent, documents, corpusDir, ai.MaxOutputTokens);
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

    // Only ever appears in *our own* RAG context formatting (see TextIndex.FormatResults) —
    // a small local model sometimes echoes it verbatim or, worse, keeps inventing further
    // "[Document N]" sections with fabricated file names and facts. Its presence at all is
    // always a failure, so this cuts the output the instant it shows up rather than
    // waiting to see whether it repeats.
    private static readonly Regex DocumentLabelPattern = new(@"\[Document\s+\d+\]", RegexOptions.Compiled);

    // A retrieved passage can itself be a letter/memo with its own greeting or signature.
    // A well-formed answer never legitimately opens with one of these on its own line, or
    // closes with a sign-off — so both are treated as structural, not content, and are
    // suppressed/cut rather than left to the model's instruction-following (which proved
    // unreliable — even naming these patterns in the system prompt as "don't do this"
    // primed a small model into reproducing them).
    private static readonly Regex BareGreetingPattern = new(@"^Dear\s+[^,]{1,40},?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> SignOffParagraphs = new(StringComparer.OrdinalIgnoreCase)
    {
        "sincerely,", "sincerely", "regards,", "regards", "best regards,", "best regards",
        "warm regards,", "warm regards", "thank you,", "respectfully,", "respectfully",
    };

    // Our own RAG prompt uses a bare "---" line as a section separator (see Ask below) —
    // a model echoing that back is the same context-leakage failure as a "[Document N]"
    // label, just a different literal string.
    private static readonly Regex SeparatorLinePattern = new(@"^-{3,}$", RegexOptions.Compiled);

    private const int RepeatLookback = 6;

    /// <summary>
    /// Some Foundry Local backends don't actually stream token-by-token — the entire
    /// response can arrive as a single update. That means "yield the chunk, then check
    /// it for problems" never works: by the time the check runs, the bad content has
    /// already been handed to the caller. So on every update, this figures out the
    /// furthest point in the *whole buffer so far* that is safe to release, and only
    /// yields up to there — checking always precedes yielding, never follows it.
    /// </summary>
    private async IAsyncEnumerable<string> StreamResponse(
        string prompt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new StringBuilder();
        int released = 0;
        bool stopped = false;

        await foreach (AgentResponseUpdate update in _agent.RunStreamingAsync(prompt, options: _runOptions, cancellationToken: ct))
        {
            if (string.IsNullOrEmpty(update.Text))
                continue;

            buffer.Append(update.Text.Replace("\r\n", "\n"));

            var (yieldEnd, newReleased, stop) = FindSafeRelease(buffer.ToString(), released, isFinal: false);
            if (yieldEnd > released)
                yield return buffer.ToString(released, yieldEnd - released);
            released = newReleased;

            if (stop)
            {
                stopped = true;
                yield break;
            }
        }

        if (stopped)
            yield break;

        // Generation is fully done: re-evaluate the trailing text once more. A backend
        // that delivered everything in one shot never had a "still being generated"
        // tail to hold back, so this is where its only real check happens.
        var (finalYieldEnd, _, _) = FindSafeRelease(buffer.ToString(), released, isFinal: true);
        if (finalYieldEnd > released)
            yield return buffer.ToString(released, finalYieldEnd - released);
    }

    /// <summary>
    /// Given the full response text so far and how much of it has already been released
    /// to the caller, decides what to do next. Returns the raw index up to which new text
    /// should actually be *yielded* to the caller (<c>YieldEnd</c>), the raw index the
    /// release cursor should *advance* to (<c>NewReleased</c> — can be further than
    /// <c>YieldEnd</c> when a span, like a leading greeting, is being silently skipped
    /// rather than shown), and whether generation should stop there. <paramref name="isFinal"/>
    /// means generation has actually finished, so even a not-yet-blank-line-terminated
    /// trailing paragraph counts as "complete" for evaluation purposes.
    /// </summary>
    private static (int YieldEnd, int NewReleased, bool Stop) FindSafeRelease(string text, int released, bool isFinal)
    {
        var labelMatch = DocumentLabelPattern.Match(text, released);
        int cut = labelMatch.Success ? labelMatch.Index : -1;

        var allParagraphs = ParagraphsWithStarts(text, isFinal);
        foreach (var (paraText, start) in allParagraphs)
        {
            if (start < released)
                continue;

            bool isBad = SignOffParagraphs.Contains(paraText) ||
                IsRepeatOfEarlier(paraText, start, allParagraphs) ||
                SeparatorLinePattern.IsMatch(paraText);
            if (isBad && (cut < 0 || start < cut))
                cut = start;
        }

        if (cut >= 0)
        {
            int cutAt = Math.Max(cut, released);
            return (cutAt, cutAt, true);
        }

        // Nothing bad found yet. The very first paragraph is special: skip it — advance
        // past it without ever yielding it — if it's a bare greeting, since once a span
        // is yielded it can't be recalled.
        if (released == 0 && allParagraphs.Count > 0 && BareGreetingPattern.IsMatch(allParagraphs[0].Text))
        {
            var boundaryMatch = Regex.Match(text, @"\n[ \t]*\n");
            int skipTo = boundaryMatch.Success ? boundaryMatch.Index + boundaryMatch.Length : released;
            return (released, skipTo, false);
        }

        // Otherwise release up through the last paragraph break we've seen so far (or,
        // if this is the final pass, the whole remaining text). Holding back an
        // in-progress paragraph avoids releasing a fragment we might still need to erase.
        if (isFinal)
            return (text.Length, text.Length, false);

        var lastBreak = Regex.Match(text, @"\n[ \t]*\n", RegexOptions.RightToLeft);
        int lastBoundary = lastBreak.Success ? lastBreak.Index + lastBreak.Length : released;
        int safeEnd = Math.Max(lastBoundary, released);
        return (safeEnd, safeEnd, false);
    }

    private static bool IsRepeatOfEarlier(string paraText, int start, List<(string Text, int Start)> allParagraphs)
    {
        if (paraText.Length <= 40)
            return false;

        int index = allParagraphs.FindIndex(p => p.Start == start);
        int windowStart = Math.Max(0, index - RepeatLookback);
        for (int i = index - 1; i >= windowStart; i--)
        {
            if (allParagraphs[i].Text == paraText)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Splits text into paragraphs (blank-ish-line separated, tolerating a stray-whitespace
    /// line as still "blank") along with each paragraph's raw start offset. When
    /// <paramref name="includeTrailing"/> is true, a final paragraph with no following
    /// blank line is included too (used once generation has actually finished).
    /// </summary>
    private static List<(string Text, int Start)> ParagraphsWithStarts(string text, bool includeTrailing)
    {
        var result = new List<(string, int)>();
        int paraStart = 0;
        int lineStart = 0;

        for (int i = 0; i <= text.Length; i++)
        {
            bool atEnd = i == text.Length;
            if (!atEnd && text[i] != '\n')
                continue;

            string line = text[lineStart..i];
            bool isBlank = string.IsNullOrWhiteSpace(line);

            if (isBlank || (atEnd && includeTrailing))
            {
                if (i > paraStart)
                {
                    // Normalize internal line wrapping to single spaces so two
                    // occurrences of the same paragraph compare equal even if the
                    // model wrapped them at different line lengths.
                    string paraText = Regex.Replace(text[paraStart..i], @"\s*\n\s*", " ").Trim();
                    if (paraText.Length > 0)
                        result.Add((paraText, paraStart));
                }
                paraStart = i + 1;
            }

            lineStart = i + 1;
        }

        return result;
    }
}

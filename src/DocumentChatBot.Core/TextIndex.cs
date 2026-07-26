using System.Text.RegularExpressions;

namespace DocumentChatBot;

public class TextIndex
{
    private readonly List<DocumentChunk> _chunks = new();
    private readonly List<Dictionary<string, double>> _tfVectors = new();
    private readonly Dictionary<string, int> _documentFrequency = new();

    public int ChunkCount => _chunks.Count;

    public void Build(List<DocumentChunk> chunks)
    {
        Console.Write("Building search index...");

        foreach (var chunk in chunks)
        {
            var terms = Tokenize(chunk.Content);
            var tf = ComputeTF(terms);

            foreach (var term in tf.Keys)
                _documentFrequency[term] = _documentFrequency.GetValueOrDefault(term, 0) + 1;

            _chunks.Add(chunk);
            _tfVectors.Add(tf);
        }

        Console.WriteLine(" done.");
    }

    public List<SearchResult> Search(string query, int topK = 5)
    {
        var queryTerms = Tokenize(query);
        var queryTf = ComputeTF(queryTerms);
        var queryVector = ToTfIdf(queryTf);

        var scored = new List<(int Index, double Score)>();

        for (int i = 0; i < _chunks.Count; i++)
        {
            var docVector = ToTfIdf(_tfVectors[i]);
            double score = CosineSimilarity(queryVector, docVector);
            if (score > 0)
                scored.Add((i, score));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .Take(topK)
            .Select(s => new SearchResult(_chunks[s.Index], s.Score))
            .ToList();
    }

    public string FormatResults(List<SearchResult> results)
    {
        if (results.Count == 0)
            return "No relevant documents found in the corpus.";

        var parts = new List<string>();
        for (int i = 0; i < results.Count; i++)
        {
            var chunk = results[i].Chunk;
            var header = $"[Document {i + 1}] {chunk.FileName}";
            if (!string.IsNullOrEmpty(chunk.Date))
                header += $" | Date: {chunk.Date}";
            if (chunk.Superseded)
                header += " | STATUS: SUPERSEDED";
            parts.Add($"{header}\n{chunk.Content}");
        }

        return string.Join("\n\n---\n\n", parts);
    }

    // --- Private helpers ---

    private Dictionary<string, double> ToTfIdf(Dictionary<string, double> tf)
    {
        int n = _chunks.Count + 1;
        var vector = new Dictionary<string, double>();

        foreach (var (term, tfScore) in tf)
        {
            if (_documentFrequency.TryGetValue(term, out int df))
            {
                double idf = Math.Log((double)n / (df + 1)) + 1.0;
                vector[term] = tfScore * idf;
            }
        }

        return vector;
    }

    private static Dictionary<string, double> ComputeTF(List<string> terms)
    {
        if (terms.Count == 0)
            return new();

        var counts = new Dictionary<string, int>();
        foreach (var term in terms)
            counts[term] = counts.GetValueOrDefault(term, 0) + 1;

        double total = terms.Count;
        return counts.ToDictionary(kv => kv.Key, kv => kv.Value / total);
    }

    private static double CosineSimilarity(
        Dictionary<string, double> a,
        Dictionary<string, double> b)
    {
        double dot = 0, normA = 0, normB = 0;

        foreach (var (term, val) in a)
        {
            normA += val * val;
            if (b.TryGetValue(term, out double bVal))
                dot += val * bVal;
        }

        foreach (var val in b.Values)
            normB += val * val;

        return (normA == 0 || normB == 0) ? 0 : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    private static List<string> Tokenize(string text) =>
        Regex.Split(text.ToLowerInvariant(), @"[^a-z0-9]+")
             .Where(t => t.Length > 2)
             .ToList();
}

public record SearchResult(DocumentChunk Chunk, double Score);

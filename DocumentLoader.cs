using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PdfSharpCore.Pdf.IO;
using PdfSharpCore.Pdf.Content;
using PdfSharpCore.Pdf.Content.Objects;

namespace DocumentChatBot;

public record DocumentChunk(
    string FileName,
    string Content,
    string Date,
    string DocType,
    bool Superseded
);

public static class DocumentLoader
{
    private const int ChunkSize = 400;
    private const int ChunkOverlap = 50;

    public static List<DocumentChunk> LoadCorpus(string corpusDir)
    {
        var chunks = new List<DocumentChunk>();
        var extensions = new[] { ".txt", ".pdf", ".docx" };

        var files = Directory.GetFiles(corpusDir)
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLower()))
            .ToArray();

        Console.WriteLine($"Loading {files.Length} documents...");

        foreach (var file in files)
        {
            var text = ReadFile(file);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var meta = ParseMetadata(Path.GetFileName(file));
            var fileChunks = ChunkText(text, meta);
            chunks.AddRange(fileChunks);

            Console.WriteLine($"  {Path.GetFileName(file)} -> {fileChunks.Count} chunks");
        }

        Console.WriteLine($"Index ready: {chunks.Count} total chunks from {files.Length} documents.");
        return chunks;
    }

    private static string ReadFile(string path)
    {
        var ext = Path.GetExtension(path).ToLower();
        try
        {
            return ext switch
            {
                ".txt"  => File.ReadAllText(path, Encoding.UTF8),
                ".pdf"  => ReadPdf(path),
                ".docx" => ReadDocx(path),
                _       => string.Empty
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  Warning: Could not read {Path.GetFileName(path)}: {ex.Message}");
            return string.Empty;
        }
    }

    private static string ReadPdf(string path)
    {
        var sb = new StringBuilder();
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.ReadOnly);
        foreach (var page in doc.Pages)
        {
            var content = ContentReader.ReadContent(page);
            ExtractText(content, sb);
        }
        return sb.ToString();
    }

    private static void ExtractText(IEnumerable<CObject> objects, StringBuilder sb)
    {
        foreach (var obj in objects)
        {
            if (obj is COperator op)
            {
                if (op.OpCode.Name is "Tj" or "TJ" or "'")
                {
                    foreach (var operand in op.Operands)
                    {
                        if (operand is CString str)
                            sb.Append(str.Value);
                        else if (operand is CArray arr)
                            foreach (var item in arr)
                                if (item is CString s)
                                    sb.Append(s.Value);
                    }
                    sb.AppendLine();
                }
            }
            else if (obj is CSequence seq)
                ExtractText(seq, sb);
        }
    }

    private static string ReadDocx(string path)
    {
        var sb = new StringBuilder();
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return string.Empty;
        foreach (var para in body.Descendants<Paragraph>())
            sb.AppendLine(para.InnerText);
        return sb.ToString();
    }

    private static List<DocumentChunk> ChunkText(
        string text,
        (string FileName, string Date, string DocType, bool Superseded) meta)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<DocumentChunk>();
        int start = 0;

        while (start < words.Length)
        {
            int end = Math.Min(start + ChunkSize, words.Length);
            var content = string.Join(' ', words[start..end]);
            chunks.Add(new DocumentChunk(
                meta.FileName, content, meta.Date, meta.DocType, meta.Superseded));
            start += ChunkSize - ChunkOverlap;
        }

        return chunks;
    }

    private static (string FileName, string Date, string DocType, bool Superseded)
        ParseMetadata(string filename)
    {
        var name = Path.GetFileNameWithoutExtension(filename);
        var parts = name.Split('_').ToList();

        string date = string.Empty;
        string docType = "unknown";
        bool superseded = false;

        if (parts.Count > 0 && Regex.IsMatch(parts[0], @"^\d{4}-\d{2}-\d{2}$"))
        {
            date = parts[0];
            parts.RemoveAt(0);
        }

        foreach (var knownType in new[] { "comment_letter", "position_paper", "guidance", "brief" })
        {
            var typeTokens = knownType.Split('_');
            if (parts.Count >= typeTokens.Length &&
                parts.Take(typeTokens.Length).SequenceEqual(typeTokens))
            {
                docType = knownType;
                parts.RemoveRange(0, typeTokens.Length);
                break;
            }
        }

        if (parts.Count > 0 && parts[^1].ToLower() == "superseded")
        {
            superseded = true;
            parts.RemoveAt(parts.Count - 1);
        }

        return (filename, date, docType, superseded);
    }
}

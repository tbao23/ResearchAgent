namespace DocumentChatBot;

public class AiSettings
{
    public string Endpoint { get; set; } = "http://127.0.0.1:53535/v1";
    public string ApiKey { get; set; } = "foundry-local";
    public string Model { get; set; } = "phi-4-mini";
    public string AgentName { get; set; } = "RegulatoryAssistant";
    public string Instructions { get; set; } = "";
    public int LoadTimeToLiveSeconds { get; set; } = 3600;
    public int MaxOutputTokens { get; set; } = 4000;
}

public class CorpusSettings
{
    public string Directory { get; set; } = "data/corpus";
}

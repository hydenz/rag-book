namespace RagBook.Api.Models;

public record HistoryMessage(string Role, string Content);

public class ChatRequest
{
    public string? Message { get; set; }
    public List<HistoryMessage>? History { get; set; }
}

public record SourceDto(int Id, string Content, double Similarity);

public record ChunkDto(int Id, string Content);

public record ChatResponse(string Reply, string Context, List<SourceDto> Sources);

using Npgsql;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;
using Pgvector;
using RagBook.Api.Models;

namespace RagBook.Api.Services;

public class RagService
{
    private const int EmbeddingDimension = 1536;
    private const string Title = "The Whispering Archive";

    private readonly NpgsqlDataSource _db;
    private readonly ChatClient _chat;
    private readonly ChatClient _story;
    private readonly EmbeddingClient _embeddings;

    public RagService(NpgsqlDataSource db, OpenAIClient openai, IConfiguration configuration)
    {
        _db = db;
        _chat = openai.GetChatClient(configuration["OpenAI:ChatModel"] ?? "gpt-4o-mini");
        _story = openai.GetChatClient(configuration["OpenAI:StoryModel"] ?? "gpt-4o-mini");
        _embeddings = openai.GetEmbeddingClient(configuration["OpenAI:EmbeddingModel"] ?? "text-embedding-3-small");
    }

    public async Task InitializeAsync()
    {
        var sql = $"""
            CREATE EXTENSION IF NOT EXISTS vector;
            CREATE TABLE IF NOT EXISTS stories (
                id SERIAL PRIMARY KEY,
                title TEXT,
                content TEXT NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS story_chunks (
                id SERIAL PRIMARY KEY,
                content TEXT NOT NULL,
                embedding vector({EmbeddingDimension})
            );
            CREATE INDEX IF NOT EXISTS story_chunks_embedding_idx
                ON story_chunks USING hnsw (embedding vector_cosine_ops);
            """;

        await using var command = _db.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    // Cost controls: cap completion length (avoid runaway generations) and
    // throttle regeneration (each regen re-embeds every chunk, which costs money too).
    private const int StoryMaxOutputTokens = 2500;
    private const int ChatMaxOutputTokens = 500;
    private static readonly TimeSpan RegenerateCooldown = TimeSpan.FromSeconds(30);
    private DateTimeOffset _lastGeneratedAt = DateTimeOffset.MinValue;

    public async Task<string> GenerateStoryAsync()
    {
        var sinceLast = DateTimeOffset.UtcNow - _lastGeneratedAt;
        if (sinceLast < RegenerateCooldown)
        {
            var wait = RegenerateCooldown - sinceLast;
            throw new InvalidOperationException(
                $"Please wait {Math.Ceiling(wait.TotalSeconds)}s before generating another story.");
        }
        _lastGeneratedAt = DateTimeOffset.UtcNow;

        var completion = await _story.CompleteChatAsync(
            [
                ChatMessage.CreateSystemMessage(
                    "You are a creative fiction writer. Write an engaging, self-contained short story."),
                ChatMessage.CreateUserMessage(
                    $"Write a fiction short story titled \"{Title}\". It should be around 1500-2500 words, " +
                    "with clear characters, a setting, and a plot twist. Write it as plain prose with paragraph breaks."),
            ],
            new ChatCompletionOptions { MaxOutputTokenCount = StoryMaxOutputTokens });

        var story = completion.Value.Content[0].Text!.Trim();

        await using (var command = _db.CreateCommand("DELETE FROM story_chunks"))
            await command.ExecuteNonQueryAsync();

        await using (var command = _db.CreateCommand("DELETE FROM stories"))
            await command.ExecuteNonQueryAsync();

        int storyId;
        await using (var command = _db.CreateCommand("INSERT INTO stories (title, content) VALUES (@title, @content) RETURNING id"))
        {
            command.Parameters.AddWithValue("title", Title);
            command.Parameters.AddWithValue("content", story);
            storyId = (int)(await command.ExecuteScalarAsync())!;
        }

        var chunks = ChunkText(story);
        foreach (var chunk in chunks)
        {
            var vector = await EmbedAsync(chunk);
            await using var command = _db.CreateCommand(
                "INSERT INTO story_chunks (content, embedding) VALUES (@content, @embedding)");
            command.Parameters.AddWithValue("content", chunk);
            command.Parameters.AddWithValue("embedding", new Vector(vector));
            await command.ExecuteNonQueryAsync();
        }

        Console.WriteLine($"Story generated: {storyId}, {chunks.Count} chunks embedded.");
        return story;
    }

    public async Task<string> GetStoryOrGenerateAsync()
    {
        await using var command = _db.CreateCommand("SELECT content FROM stories ORDER BY id DESC LIMIT 1");
        var content = await command.ExecuteScalarAsync() as string;
        if (!string.IsNullOrEmpty(content))
            return content;

        return await GenerateStoryAsync();
    }

    public async Task<List<ChunkDto>> GetChunksAsync()
    {
        var chunks = new List<ChunkDto>();
        await using var command = _db.CreateCommand("SELECT id, content FROM story_chunks ORDER BY id");
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            chunks.Add(new ChunkDto(reader.GetInt32(0), reader.GetString(1)));

        return chunks;
    }

    // How many prior turns to resend as context. Keeps token cost from growing unbounded
    // as a conversation gets longer.
    private const int MaxHistoryMessages = 8;

    public async Task<ChatResponse> ChatWithRagAsync(string message, List<HistoryMessage> history)
    {
        var passages = await RetrieveAsync(message);
        var context = string.Join("\n\n", passages.Select((p, i) => $"[Passage {i + 1}]\n{p.Content}"));

        var messages = new List<ChatMessage>
        {
            // Grounding comes from the retrieved passages below, not the full story text —
            // resending the whole story on every turn was pure wasted tokens (RAG's job is
            // to fetch only what's relevant).
            ChatMessage.CreateSystemMessage("""
                You are an assistant that answers questions ONLY about a fiction story that was
                written by an AI. Use the retrieved passages provided with each question as your
                ground truth. If the answer is not in the passages, say you don't know. Never
                invent details.
                """)
        };

        foreach (var historyMessage in history.TakeLast(MaxHistoryMessages))
        {
            messages.Add(historyMessage.Role == "user"
                ? ChatMessage.CreateUserMessage(historyMessage.Content)
                : ChatMessage.CreateAssistantMessage(historyMessage.Content));
        }

        messages.Add(ChatMessage.CreateUserMessage($"Question: {message}\n\nRelevant passages from the RAG:\n{context}"));

        var completion = await _chat.CompleteChatAsync(
            messages,
            new ChatCompletionOptions { MaxOutputTokenCount = ChatMaxOutputTokens });

        return new ChatResponse(
            completion.Value.Content[0].Text!,
            context,
            passages);
    }

    private async Task<List<SourceDto>> RetrieveAsync(string query, int k = 4)
    {
        var vector = await EmbedAsync(query);

        var sources = new List<SourceDto>();
        await using var command = _db.CreateCommand(
            @"SELECT id, content, 1 - (embedding <=> @query) AS similarity
              FROM story_chunks
              ORDER BY embedding <=> @query
              LIMIT @k");
        command.Parameters.AddWithValue("query", new Vector(vector));
        command.Parameters.AddWithValue("k", k);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            sources.Add(new SourceDto(reader.GetInt32(0), reader.GetString(1), reader.GetDouble(2)));

        return sources;
    }

    private async Task<float[]> EmbedAsync(string text)
    {
        var result = await _embeddings.GenerateEmbeddingAsync(text);
        return result.Value.ToFloats().ToArray();
    }

    private static List<string> ChunkText(string text, int chunkSize = 1000, int overlap = 200)
    {
        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(start + chunkSize, text.Length);
            if (end < text.Length)
            {
                var nl = text.LastIndexOf("\n\n", end, StringComparison.Ordinal);
                if (nl > start + chunkSize / 2)
                    end = nl;
            }

            chunks.Add(text[start..end].Trim());
            if (end >= text.Length)
                break;

            start = Math.Max(start + 1, end - overlap);
        }

        return chunks.Where(c => c.Length > 0).ToList();
    }
}

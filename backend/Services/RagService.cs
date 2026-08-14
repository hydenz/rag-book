using System.Collections.Concurrent;
using System.Text.RegularExpressions;
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
        // On a brand-new database, this is the data source's first connection —
        // Npgsql builds its type map (which extension types exist) as that
        // connection opens, before CREATE EXTENSION below has actually run. DDL
        // text doesn't need type binding so table/index creation is unaffected,
        // but any later query binding a Pgvector.Vector parameter would fail
        // ("not supported for parameters having no NpgsqlDbType") because
        // Npgsql's cached map still says `vector` doesn't exist. Create the
        // extension on its own first, then force a reload before anything else.
        await using (var extensionCommand = _db.CreateCommand("CREATE EXTENSION IF NOT EXISTS vector;"))
            await extensionCommand.ExecuteNonQueryAsync();
        await _db.ReloadTypesAsync();

        // session_id scopes stories/chunks per browser (see frontend's
        // src/lib/session.ts) so one visitor regenerating doesn't change the
        // story out from under everyone else's conversation. ADD COLUMN IF NOT
        // EXISTS covers databases that already had these tables from before
        // this migration; CREATE TABLE already includes it for fresh ones.
        // Pre-migration rows have a NULL session_id, which never matches any
        // real session's WHERE clause — clean them up rather than leave dead
        // rows around.
        var sql = $"""
            CREATE TABLE IF NOT EXISTS stories (
                id SERIAL PRIMARY KEY,
                session_id TEXT,
                title TEXT,
                content TEXT NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS story_chunks (
                id SERIAL PRIMARY KEY,
                session_id TEXT,
                content TEXT NOT NULL,
                embedding vector({EmbeddingDimension})
            );
            ALTER TABLE stories ADD COLUMN IF NOT EXISTS session_id TEXT;
            ALTER TABLE story_chunks ADD COLUMN IF NOT EXISTS session_id TEXT;
            CREATE INDEX IF NOT EXISTS story_chunks_embedding_idx
                ON story_chunks USING hnsw (embedding vector_cosine_ops);
            CREATE INDEX IF NOT EXISTS story_chunks_session_idx ON story_chunks (session_id);
            CREATE INDEX IF NOT EXISTS stories_session_idx ON stories (session_id);
            DELETE FROM story_chunks WHERE session_id IS NULL;
            DELETE FROM stories WHERE session_id IS NULL;
            """;

        await using var command = _db.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    // Cost controls: cap completion length (avoid runaway generations) and
    // throttle regeneration (each regen re-embeds every chunk, which costs money too).
    // Per-session, not global, now that each session has its own story — otherwise
    // one visitor's regenerate would block everyone else's for 30s. This dictionary
    // grows for the life of the process (one entry per session that's ever
    // generated); fine at this app's scale, would need eviction for a long-running
    // deploy with heavy traffic.
    private const int StoryMaxOutputTokens = 2500;
    private const int ChatMaxOutputTokens = 500;
    private static readonly TimeSpan RegenerateCooldown = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastGeneratedAt = new();

    public async Task<string> GenerateStoryAsync(string sessionId)
    {
        var lastGenerated = _lastGeneratedAt.GetValueOrDefault(sessionId, DateTimeOffset.MinValue);
        var sinceLast = DateTimeOffset.UtcNow - lastGenerated;
        if (sinceLast < RegenerateCooldown)
        {
            var wait = RegenerateCooldown - sinceLast;
            throw new InvalidOperationException(
                $"Please wait {Math.Ceiling(wait.TotalSeconds)}s before generating another story.");
        }
        _lastGeneratedAt[sessionId] = DateTimeOffset.UtcNow;

        var completion = await _story.CompleteChatAsync(
            [
                ChatMessage.CreateSystemMessage(
                    "You are a creative fiction writer. Write an engaging, self-contained short story."),
                ChatMessage.CreateUserMessage(
                    $"Write a fiction short story titled \"{Title}\". It should be around 1500-2500 words, " +
                    "with clear characters, a setting, and a plot twist. Write it as plain prose with paragraph " +
                    "breaks — no title line, no headings, no markdown formatting of any kind (no **bold**, no " +
                    "#headings, no asterisks). Start directly with the story's first sentence."),
            ],
            new ChatCompletionOptions { MaxOutputTokenCount = StoryMaxOutputTokens });

        var story = StripLeadingTitleLine(completion.Value.Content[0].Text!.Trim());

        await using (var command = _db.CreateCommand("DELETE FROM story_chunks WHERE session_id = @sessionId"))
        {
            command.Parameters.AddWithValue("sessionId", sessionId);
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = _db.CreateCommand("DELETE FROM stories WHERE session_id = @sessionId"))
        {
            command.Parameters.AddWithValue("sessionId", sessionId);
            await command.ExecuteNonQueryAsync();
        }

        int storyId;
        await using (var command = _db.CreateCommand(
            "INSERT INTO stories (session_id, title, content) VALUES (@sessionId, @title, @content) RETURNING id"))
        {
            command.Parameters.AddWithValue("sessionId", sessionId);
            command.Parameters.AddWithValue("title", Title);
            command.Parameters.AddWithValue("content", story);
            storyId = (int)(await command.ExecuteScalarAsync())!;
        }

        var chunks = ChunkText(story);
        foreach (var chunk in chunks)
        {
            var vector = await EmbedAsync(chunk);
            await using var command = _db.CreateCommand(
                "INSERT INTO story_chunks (session_id, content, embedding) VALUES (@sessionId, @content, @embedding)");
            command.Parameters.AddWithValue("sessionId", sessionId);
            command.Parameters.AddWithValue("content", chunk);
            command.Parameters.AddWithValue("embedding", new Vector(vector));
            await command.ExecuteNonQueryAsync();
        }

        Console.WriteLine($"Story generated: {storyId}, {chunks.Count} chunks embedded, session {sessionId}.");
        return story;
    }

    public async Task<string> GetStoryOrGenerateAsync(string sessionId)
    {
        await using var command = _db.CreateCommand(
            "SELECT content FROM stories WHERE session_id = @sessionId ORDER BY id DESC LIMIT 1");
        command.Parameters.AddWithValue("sessionId", sessionId);
        var content = await command.ExecuteScalarAsync() as string;
        if (!string.IsNullOrEmpty(content))
            return content;

        return await GenerateStoryAsync(sessionId);
    }

    public async Task<List<ChunkDto>> GetChunksAsync(string sessionId)
    {
        var chunks = new List<ChunkDto>();
        await using var command = _db.CreateCommand(
            "SELECT id, content FROM story_chunks WHERE session_id = @sessionId ORDER BY id");
        command.Parameters.AddWithValue("sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            chunks.Add(new ChunkDto(reader.GetInt32(0), reader.GetString(1)));

        return chunks;
    }

    // How many prior turns to resend as context. Keeps token cost from growing unbounded
    // as a conversation gets longer.
    private const int MaxHistoryMessages = 8;

    public async Task<ChatResponse> ChatWithRagAsync(string sessionId, string message, List<HistoryMessage> history)
    {
        var passages = await RetrieveAsync(sessionId, message);
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

    private async Task<List<SourceDto>> RetrieveAsync(string sessionId, string query, int k = 4)
    {
        var vector = await EmbedAsync(query);

        var sources = new List<SourceDto>();
        await using var command = _db.CreateCommand(
            @"SELECT id, content, 1 - (embedding <=> @query) AS similarity
              FROM story_chunks
              WHERE session_id = @sessionId
              ORDER BY embedding <=> @query
              LIMIT @k");
        command.Parameters.AddWithValue("sessionId", sessionId);
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

    // GPT sometimes prepends a bold or heading-style title line despite being
    // told not to (e.g. "**The Whispering Archive**\n\n..."). We don't render
    // markdown, so leftover asterisks/hashes would show up as literal
    // characters in the prose — strip a leading title-shaped line if present.
    private static string StripLeadingTitleLine(string story)
    {
        var match = Regex.Match(story, @"^\s*(\*\*[^\n]+\*\*|#{1,6}[^\n]+)\s*\n+");
        return match.Success ? story[match.Length..].TrimStart() : story;
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

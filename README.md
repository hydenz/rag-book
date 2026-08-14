# The Whispering Archive — Learn RAG

A small full-stack learning project to understand **Retrieval-Augmented Generation (RAG)**.

- **Frontend:** React 19 + Vite + TypeScript — two tabs: *Manuscript* (AI-generated fiction) and *Consult the Archive* (ask questions about the story, grounded via RAG).
- **Backend:** ASP.NET Core (.NET 8) minimal API + OpenAI (GPT chat + embeddings).
- **Database:** PostgreSQL with the `pgvector` extension for vector similarity search.

## How it works

1. `POST /api/story/generate` asks GPT (`gpt-4o-mini` by default) to write a short fiction story.
2. The story is split into overlapping chunks, each chunk is embedded (`text-embedding-3-small`) and stored in Postgres `story_chunks`, indexed with an HNSW index.
3. `POST /api/chat` embeds your question, retrieves the most similar chunks via pgvector (`<=>` cosine distance), passes them as grounding context, and has GPT answer using only those passages.

## Prerequisites

- Node.js 20+
- .NET 8 SDK
- PostgreSQL with the `pgvector` extension (a ready-to-use container is provided, see below)

## Setup & run

1. Start Postgres (with pgvector) via Docker:

   ```bash
   docker compose up -d
   ```

   This brings up `pgvector/pgvector:pg16` on `localhost:5432` (db `ragbook`, user/password `postgres`/`postgres` — matches `backend/appsettings.json`'s default connection string). The backend creates its own tables and the HNSW index on startup, so no manual schema step is needed.

2. Configure the backend's OpenAI API key (dev):

   ```bash
   cd backend
   dotnet user-secrets set "OpenAI:ApiKey" "sk-your-key"
   ```

   User secrets are only loaded in the `Development` environment and are stored
   outside the repo, so the key is never committed. Alternatively, set
   `OpenAI:ApiKey` in `backend/appsettings.Development.json` (gitignored) or the
   `OPENAI_API_KEY` environment variable.

3. Start the backend (listens on `http://localhost:3001` by default, override with the `PORT` env var):

   ```bash
   cd backend
   dotnet run
   ```

4. Start the frontend (separate terminal):

   ```bash
   cd frontend
   npm install
   npm run dev
   ```

   Vite proxies `/api` to `http://localhost:3001` (see `frontend/vite.config.js`).

5. Open http://localhost:5173, click **Write a new manuscript** in the Manuscript tab, then ask questions in **Consult the Archive**.

## API

| Method | Path                  | Body                    | Description                                         |
| ------ | --------------------- | ------------------------ | --------------------------------------------------- |
| GET    | `/api/health`          | —                        | Health check                                         |
| GET    | `/api/story`           | —                        | Return the stored story (generates one if missing)   |
| POST   | `/api/story/generate`  | —                        | Regenerate the story and re-embed its chunks         |
| GET    | `/api/chunks`          | —                        | List all stored chunks                               |
| POST   | `/api/chat`            | `{ message, history }`   | RAG answer + retrieved sources                       |

`/api/chat` and `/api/story/generate` are rate limited (3 requests/min per IP) and cap completion length, since both call OpenAI directly. `/api/story/generate` also enforces a 30s cooldown between regenerations (each one re-embeds every chunk). All are cost controls, not correctness constraints — see `backend/Program.cs` / `backend/Services/RagService.cs`.

## Project layout

- `backend/` — ASP.NET Core API. `Program.cs` wires up endpoints, CORS, and rate limiting; `Services/RagService.cs` has story generation, chunking, embedding, retrieval, and the RAG chat prompt; `Models/Models.cs` has the request/response DTOs.
- `frontend/` — React + TypeScript app. `src/types/api.ts` mirrors the backend DTOs (no OpenAPI spec exists, kept in sync by hand); `src/lib/ragTransport.ts` bridges the `ai` SDK's chat transport to the backend's REST API; `src/components/` has the two tabs.
- `frontend/e2e-test.mjs` — a standalone Node script that hits a running backend directly and asserts the `/api/chat` response/stream shape. Run with `node e2e-test.mjs` while the backend is up.
- `docker-compose.yml` — local Postgres + pgvector.

## Learning notes

- Table creation and the HNSW index live in `RagService.InitializeAsync` (`backend/Services/RagService.cs`) — run on every backend startup, idempotent (`CREATE TABLE IF NOT EXISTS`).
- Chunking (`ChunkText`), retrieval (`RetrieveAsync`), and prompt assembly (`ChatWithRagAsync`) are all in `backend/Services/RagService.cs`.
- Changing the embedding dimension (`EmbeddingDimension` constant, currently 1536 for `text-embedding-3-small`) requires recreating the `story_chunks` table.

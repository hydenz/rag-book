# RAG Book — Learn RAG with React 19

A small full-stack learning project to understand **Retrieval-Augmented Generation (RAG)**.

- **Frontend:** React 19 + Vite — two tabs: *Story* (AI-generated fiction) and *RAG Chat* (ask questions about the story).
- **Backend:** Express + OpenAI (GPT chat + embeddings).
- **Database:** PostgreSQL with the `pgvector` extension for vector similarity search.

## How it works

1. `POST /api/story/generate` asks GPT to write a short fiction story.
2. The story is split into chunks, each chunk is embedded (`text-embedding-3-small`) and stored in Postgres `story_chunks`.
3. `POST /api/chat` embeds your question, retrieves the most similar chunks via pgvector (`<=>` cosine distance), injects them as context, and has GPT answer grounded in the story.

## Prerequisites

- Node.js 20+
- PostgreSQL with the `pgvector` extension:
  ```sql
  CREATE EXTENSION IF NOT EXISTS vector;
  ```

## Setup & run

1. Configure the server:

   ```bash
   cp server/.env.example server/.env
   # edit server/.env — set a real OPENAI_API_KEY and a working DATABASE_URL
   ```

2. Create the database:

   ```bash
   createdb ragbook
   psql ragbook -c "CREATE EXTENSION IF NOT EXISTS vector;"
   psql ragbook -f server/schema.sql
   ```

3. Start the backend:

   ```bash
   cd server && npm install && npm run dev
   ```

4. Start the frontend (separate terminal):

   ```bash
   cd client && npm install && npm run dev
   ```

5. Open http://localhost:5173, hit **Generate new story** in the Story tab, then ask questions in the **RAG Chat** tab.

## API

| Method | Path                 | Body                     | Description                     |
| ------ | -------------------- | ------------------------ | ------------------------------- |
| GET    | `/api/story`         | —                        | Return the stored story (generates one if missing) |
| POST   | `/api/story/generate`| —                        | Regenerate story and re-embed |
| POST   | `/api/chat`          | `{ message, history }`   | RAG answer + retrieved sources |

## Learning notes

- Embedding + HNSW index lives in `server/schema.sql`.
- Chunking (`chunkText`), retrieval (`search`) and prompt assembly (`chatWithRag`) are in `server/rag.js`.
- Changing `EMBEDDING_DIM` requires recreating the `story_chunks` table.
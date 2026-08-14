// Mirrors backend/Models/Models.cs — no OpenAPI spec exists, keep in sync by hand.

export interface HistoryMessage {
  role: string;
  content: string;
}

export interface ChatRequest {
  message?: string;
  history?: HistoryMessage[];
}

export interface SourceDto {
  id: number;
  content: string;
  similarity: number;
}

export interface ChunkDto {
  id: number;
  content: string;
}

export interface ChatResponse {
  reply: string;
  context: string;
  sources: SourceDto[];
}

export interface ApiErrorResponse {
  error: string;
}

export interface StoryResponse {
  story: string;
}

export interface ChunksResponse {
  count: number;
  chunks: ChunkDto[];
}

export interface HealthResponse {
  ok: true;
}

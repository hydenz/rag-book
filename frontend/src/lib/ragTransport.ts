import { DefaultChatTransport, type UIMessageChunk } from "ai";
import { RAG_SOURCES_TYPE, type AppUIMessage, type RagDataParts } from "../types/chat";
import type { ChatResponse, ApiErrorResponse, HistoryMessage } from "../types/api";
import { getSessionId } from "./session";

function extractText(message: AppUIMessage): string {
  if (Array.isArray(message.parts)) {
    const text = message.parts
      .filter((part) => part.type === "text")
      .map((part) => part.text)
      .join("");
    if (text) return text;
  }
  return "";
}

async function ragChatFetch(_url: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const body: { messages?: AppUIMessage[] } = JSON.parse((init?.body as string) ?? "{}");
  const messages = body.messages ?? [];
  const last = messages[messages.length - 1];
  const history: HistoryMessage[] = messages.slice(0, -1).map((m) => ({
    role: m.role === "assistant" ? "assistant" : "user",
    content: extractText(m),
  }));

  const res = await fetch("/api/chat", {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-Session-Id": getSessionId() },
    signal: init?.signal,
    body: JSON.stringify({ message: last ? extractText(last) : "", history }),
  });

  if (!res.ok) {
    let detail = `Chat failed (${res.status})`;
    try {
      const data = (await res.json()) as ApiErrorResponse;
      detail = data.error ?? detail;
    } catch {}
    return new Response(detail, { status: res.status });
  }

  const data = (await res.json()) as ChatResponse;
  const reply = data.reply ?? "";
  const sources = data.sources ?? [];

  const encoder = new TextEncoder();
  const partId = `part_${crypto.randomUUID()}`;
  const paragraphs = reply.split(/\n{2,}/).filter((p) => p.trim());

  const stream = new ReadableStream({
    start(controller) {
      const emit = (chunk: UIMessageChunk<unknown, RagDataParts>) =>
        controller.enqueue(encoder.encode(`data: ${JSON.stringify(chunk)}\n\n`));

      emit({ type: "text-start", id: partId });
      paragraphs.forEach((paragraph, i) => {
        emit({
          type: "text-delta",
          id: partId,
          delta: paragraph.trim() + (i < paragraphs.length - 1 ? "\n\n" : ""),
        });
      });
      emit({ type: "text-end", id: partId });
      emit({ type: RAG_SOURCES_TYPE, data: { sources } });
      emit({ type: "finish", finishReason: "stop" });
      controller.close();
    },
  });

  return new Response(stream, {
    status: 200,
    headers: { "Content-Type": "text/event-stream" },
  });
}

export const ragChatTransport = new DefaultChatTransport<AppUIMessage>({
  api: "/api/chat",
  fetch: ragChatFetch,
});

export { RAG_SOURCES_TYPE };

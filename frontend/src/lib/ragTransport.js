import { DefaultChatTransport } from "ai";

export const RAG_SOURCES_TYPE = "data-rag-sources";

function extractText(message) {
  if (Array.isArray(message.parts)) {
    const text = message.parts
      .filter((part) => part.type === "text")
      .map((part) => part.text)
      .join("");
    if (text) return text;
  }
  return message.content ?? "";
}

async function ragChatFetch(url, init) {
  const body = JSON.parse(init?.body ?? "{}");
  const messages = body.messages ?? [];
  const last = messages[messages.length - 1];
  const history = messages.slice(0, -1).map((m) => ({
    role: m.role === "assistant" ? "assistant" : "user",
    content: extractText(m),
  }));

  const res = await fetch("/api/chat", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    signal: init?.signal,
    body: JSON.stringify({ message: extractText(last), history }),
  });

  if (!res.ok) {
    let detail = `Chat failed (${res.status})`;
    try {
      const data = await res.json();
      detail = data.error ?? detail;
    } catch {}
    return new Response(detail, { status: res.status });
  }

  const data = await res.json();
  const reply = data.reply ?? "";
  const sources = data.sources ?? [];

  const encoder = new TextEncoder();
  const partId = `part_${crypto.randomUUID()}`;
  const paragraphs = reply.split(/\n{2,}/).filter((p) => p.trim());

  const stream = new ReadableStream({
    start(controller) {
      const emit = (chunk) =>
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

export const ragChatTransport = new DefaultChatTransport({
  api: "/api/chat",
  fetch: ragChatFetch,
});

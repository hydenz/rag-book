import { useEffect, useRef, useState, type SubmitEvent, type ChangeEvent } from "react";
import { useChat } from "@ai-sdk/react";
import type { DataUIPart } from "ai";
import { ragChatTransport, RAG_SOURCES_TYPE } from "../lib/ragTransport";
import type { AppUIMessage, RagDataParts } from "../types/chat";

function messageText(message: AppUIMessage): string {
  return (message.parts ?? [])
    .filter((part) => part.type === "text")
    .map((part) => part.text)
    .join("");
}

interface ChatTabProps {
  storyReady: boolean;
}

export default function ChatTab({ storyReady }: ChatTabProps) {
  const [input, setInput] = useState("");
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const { messages, sendMessage, stop, status, error } = useChat<AppUIMessage>({
    transport: ragChatTransport,
  });

  const busy = status === "submitted" || status === "streaming";

  useEffect(() => {
    scrollRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages]);

  const onSubmit = (e: SubmitEvent<HTMLFormElement>) => {
    e.preventDefault();
    const text = input.trim();
    if (!text || busy) return;
    setInput("");
    sendMessage({ text });
  };

  const lastAssistant = [...messages].reverse().find((m) => m.role === "assistant");
  const isSourcesPart = (
    part: AppUIMessage["parts"][number]
  ): part is DataUIPart<RagDataParts> => part.type === RAG_SOURCES_TYPE;
  const sources = lastAssistant?.parts?.filter(isSourcesPart).at(-1)?.data.sources ?? [];

  return (
    <div className="chat-tab">
      {!storyReady && (
        <p className="status-line">
          Open or write a manuscript first — the archivist needs something to consult.
        </p>
      )}

      <div className="chat-window">
        {messages.length === 0 && (
          <p className="muted placeholder">
            Ask anything about the manuscript. The archivist answers only from
            passages it can find and point to — never from memory.
          </p>
        )}
        {messages.map((m) => (
          <div key={m.id} className={`msg ${m.role}`}>
            <span className="who">{m.role === "user" ? "You" : "The Archivist"}</span>
            {messageText(m)}
          </div>
        ))}
        {error && <p className="error-line">The archivist couldn't answer — {error.message}</p>}
        <div ref={scrollRef} />
      </div>

      {sources.length > 0 && (
        <details className="sources">
          <summary>Passages consulted ({sources.length})</summary>
          <div className="catalog">
            {sources.map((s, i) => (
              <div key={i} className="card">
                <span className="card-label">Passage No. {String(i + 1).padStart(3, "0")}</span>
                <p>{s.content}</p>
                <div className="stamp" aria-hidden="true">
                  {(s.similarity * 100).toFixed(1)}%<br />match
                </div>
              </div>
            ))}
          </div>
        </details>
      )}

      <form onSubmit={onSubmit} className="chat-input">
        <input
          value={input}
          onChange={(e: ChangeEvent<HTMLInputElement>) => setInput(e.target.value)}
          placeholder="e.g. Who is the main character?"
          disabled={busy}
        />
        <button type="submit" disabled={busy || !input.trim()}>
          {busy ? "Asking…" : "Ask"}
        </button>
        {busy && (
          <button type="button" className="secondary" onClick={stop} disabled={!busy}>
            Stop
          </button>
        )}
      </form>
    </div>
  );
}

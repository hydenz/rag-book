import { useEffect, useRef, useState } from "react";
import { useChat } from "@ai-sdk/react";
import { ragChatTransport, RAG_SOURCES_TYPE } from "../lib/ragTransport.js";

function messageText(message) {
  return (message.parts ?? [])
    .filter((part) => part.type === "text")
    .map((part) => part.text)
    .join("");
}

export default function ChatTab({ storyReady }) {
  const [input, setInput] = useState("");
  const scrollRef = useRef(null);
  const { messages, sendMessage, stop, status, error } = useChat({
    transport: ragChatTransport,
  });

  const busy = status === "submitted" || status === "streaming";

  useEffect(() => {
    scrollRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages]);

  const onSubmit = (e) => {
    e.preventDefault();
    const text = input.trim();
    if (!text || busy) return;
    setInput("");
    sendMessage({ role: "user", text });
  };

  const lastAssistant = [...messages].reverse().find((m) => m.role === "assistant");
  const sources =
    lastAssistant?.parts
      ?.filter((part) => part.type === RAG_SOURCES_TYPE)
      .at(-1)?.data?.sources ?? [];

  return (
    <div className="chat-tab">
      {!storyReady && (
        <p className="muted">
          Generate or load the story first so your questions can be RAGged.
        </p>
      )}

      <div className="chat-window">
        {messages.length === 0 && (
          <p className="muted placeholder">
            Ask anything about the AI-generated story. Answers are grounded in
            retrieved passages from pgvector.
          </p>
        )}
        {messages.map((m) => (
          <div key={m.id} className={`msg ${m.role}`}>
            <b>{m.role === "user" ? "You" : "RAG Bot"}:</b> {messageText(m)}
          </div>
        ))}
        {error && <p className="muted">Error: {error.message}</p>}
        <div ref={scrollRef} />
      </div>

      {sources.length > 0 && (
        <details className="sources">
          <summary>Retrieved passages ({sources.length})</summary>
          {sources.map((s, i) => (
            <blockquote key={i}>
              {s.content}
              <footer>similarity {(s.similarity * 100).toFixed(1)}%</footer>
            </blockquote>
          ))}
        </details>
      )}

      <form onSubmit={onSubmit} className="chat-input">
        <input
          value={input}
          onChange={(e) => setInput(e.target.value)}
          placeholder="e.g. Who is the main character?"
          disabled={busy}
        />
        <button type="submit" disabled={busy || !input.trim()}>
          {busy ? "…" : "Send"}
        </button>
        {busy && (
          <button type="button" onClick={stop} disabled={!busy}>
            Stop
          </button>
        )}
      </form>
    </div>
  );
}

import { useState, useCallback } from "react";
import StoryTab from "./components/StoryTab";
import ChatTab from "./components/ChatTab";
import type { StoryResponse, ApiErrorResponse } from "./types/api";

const TABS = [
  { id: "story", label: "Manuscript" },
  { id: "chat", label: "Consult the Archive" },
] as const;

type TabId = (typeof TABS)[number]["id"];

type StoryState = "idle" | "loading" | "generating";

export default function App() {
  const [tab, setTab] = useState<TabId>("story");
  const [story, setStory] = useState("");
  const [storyState, setStoryState] = useState<StoryState>("idle");
  const [storyError, setStoryError] = useState<string | null>(null);

  const loadStory = useCallback(async () => {
    setStoryState("loading");
    setStoryError(null);
    try {
      const res = await fetch("/api/story");
      const data = (await res.json()) as StoryResponse | ApiErrorResponse;
      if ("error" in data) throw new Error(data.error);
      setStory(data.story);
    } catch (err) {
      setStoryError(err instanceof Error ? err.message : "Failed to load the manuscript.");
    } finally {
      setStoryState("idle");
    }
  }, []);

  const generateNew = useCallback(async () => {
    setStoryState("generating");
    setStoryError(null);
    try {
      const res = await fetch("/api/story/generate", { method: "POST" });
      const data = (await res.json()) as StoryResponse | ApiErrorResponse;
      if ("error" in data) throw new Error(data.error);
      setStory(data.story);
    } catch (err) {
      setStoryError(err instanceof Error ? err.message : "Failed to write a new manuscript.");
    } finally {
      setStoryState("idle");
    }
  }, []);

  return (
    <div className="app">
      <header className="header">
        <div>
          <h1>The Whispering Archive</h1>
          <p>An AI writes the manuscript. You may only ask the archivist what it can prove.</p>
        </div>
        <div className="seal" aria-hidden="true">
          ✒
        </div>
      </header>

      <nav className="tabs">
        {TABS.map((t) => (
          <button
            key={t.id}
            className={`tab ${tab === t.id ? "active" : ""}`}
            onClick={() => setTab(t.id)}
          >
            {t.label}
          </button>
        ))}
      </nav>

      <main className="content">
        <div className={tab === "story" ? "" : "hidden"}>
          <StoryTab
            story={story}
            state={storyState}
            error={storyError}
            onLoad={loadStory}
            onGenerate={generateNew}
          />
        </div>
        <div className={tab === "chat" ? "" : "hidden"}>
          <ChatTab storyReady={!!story} />
        </div>
      </main>

      <footer className="colophon">React · OpenAI · pgvector — a RAG experiment</footer>
    </div>
  );
}

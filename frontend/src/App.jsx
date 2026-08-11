import { useState, useEffect, useRef, useCallback } from "react";
import StoryTab from "./components/StoryTab.jsx";
import ChatTab from "./components/ChatTab.jsx";

const TABS = [
  { id: "story", label: "Story" },
  { id: "chat", label: "RAG Chat" },
];

export default function App() {
  const [tab, setTab] = useState("story");
  const [story, setStory] = useState("");
  const [loadingStory, setLoadingStory] = useState(false);

  const loadStory = useCallback(async () => {
    setLoadingStory(true);
    try {
      const res = await fetch("/api/story");
      const data = await res.json();
      if (!res.ok) throw new Error(data.error || "Failed to load story");
      setStory(data.story);
    } finally {
      setLoadingStory(false);
    }
  }, []);

  const generateNew = useCallback(async () => {
    setLoadingStory(true);
    try {
      const res = await fetch("/api/story/generate", { method: "POST" });
      const data = await res.json();
      if (!res.ok) throw new Error(data.error || "Failed to generate story");
      setStory(data.story);
    } finally {
      setLoadingStory(false);
    }
  }, []);

  return (
    <div className="app">
      <header className="header">
        <h1>RAG Book</h1>
        <p>A React 19 + OpenAI + pgvector RAG learning project</p>
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
            loading={loadingStory}
            onLoad={loadStory}
            onGenerate={generateNew}
          />
        </div>
        <div className={tab === "chat" ? "" : "hidden"}>
          <ChatTab storyReady={!!story} />
        </div>
      </main>
    </div>
  );
}
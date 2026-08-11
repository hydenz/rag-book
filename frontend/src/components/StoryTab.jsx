import { useEffect } from "react";

export default function StoryTab({ story, loading, onLoad, onGenerate }) {
  useEffect(() => {
    if (!story) onLoad();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <div className="story-tab">
      <div className="toolbar">
        <button onClick={onLoad} disabled={loading}>
          Load story
        </button>
        <button onClick={onGenerate} disabled={loading}>
          {loading ? "Working…" : "Generate new story"}
        </button>
      </div>

      {loading && <p className="muted">Loading…</p>}

      {story && (
        <article className="story prose">
          <h2>A Story</h2>
          {story.split("\n\n").map((para, i) => (
            <p key={i}>{para}</p>
          ))}
        </article>
      )}
    </div>
  );
}
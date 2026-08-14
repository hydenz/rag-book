import { useEffect } from "react";

type StoryState = "idle" | "loading" | "generating";

interface StoryTabProps {
  story: string;
  state: StoryState;
  onLoad: () => Promise<void>;
  onGenerate: () => Promise<void>;
}

export default function StoryTab({ story, state, onLoad, onGenerate }: StoryTabProps) {
  const busy = state !== "idle";

  useEffect(() => {
    if (!story) onLoad();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <div className="story-tab">
      <div className="toolbar">
        <button className="secondary" onClick={onLoad} disabled={busy}>
          {state === "loading" ? "Opening…" : "Open manuscript"}
        </button>
        <button onClick={onGenerate} disabled={busy}>
          {state === "generating" ? "Writing…" : "Write a new manuscript"}
        </button>
      </div>

      {state === "loading" && <p className="status-line">Opening the archive…</p>}
      {state === "generating" && (
        <p className="status-line">
          The archivist is drafting a new manuscript — this can take up to a minute.
        </p>
      )}

      {story && (
        <article className="manuscript">
          <span className="running-head">The Whispering Archive</span>
          <div className="story-body">
            {story.split("\n\n").map((para, i) => (
              <p key={i} className={i === 0 ? "lede" : undefined}>
                {para}
              </p>
            ))}
          </div>
        </article>
      )}
    </div>
  );
}

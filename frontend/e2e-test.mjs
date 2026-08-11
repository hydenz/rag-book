import { EventSourceParserStream } from "eventsource-parser/stream";

const encoder = new TextEncoder();

async function buildStream(reply, sources) {
  const partId = `part_${crypto.randomUUID()}`;
  const paragraphs = reply.split(/\n{2,}/).filter((p) => p.trim());
  return new ReadableStream({
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
      emit({ type: "data-rag-sources", data: { sources } });
      emit({ type: "finish", finishReason: "stop" });
      controller.close();
    },
  });
}

const res = await fetch("http://localhost:3001/api/chat", {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify({ message: "Who is the main character?", history: [] }),
});
const data = await res.json();
console.log("backend reply (first 120 chars):", data.reply.slice(0, 120));
console.log("backend sources:", data.sources.length);

const chunks = [];
await (await buildStream(data.reply, data.sources))
  .pipeThrough(new TextDecoderStream())
  .pipeThrough(new EventSourceParserStream())
  .pipeThrough(
    new TransformStream({
      transform({ data: d }, controller) {
        controller.enqueue(d === "[DONE]" ? d : JSON.parse(d));
      },
    })
  )
  .pipeTo(
    new WritableStream({
      write(chunk) {
        chunks.push(chunk);
      },
    })
  );

const types = chunks.map((c) => c.type);
const textDeltas = chunks
  .filter((c) => c.type === "text-delta")
  .map((c) => c.delta)
  .join("");

const ok =
  types[0] === "text-start" &&
  types.includes("text-end") &&
  types.includes("data-rag-sources") &&
  types.at(-1) === "finish" &&
  chunks.at(-1).finishReason === "stop" &&
  textDeltas === data.reply &&
  chunks.find((c) => c.type === "data-rag-sources").data.sources.length ===
    data.sources.length;

console.log("chunk order:", types.join(", "));
console.log("text reassembled:", textDeltas === data.reply);
console.log(ok ? "E2E STREAM OK" : "E2E STREAM FAILED");
process.exit(ok ? 0 : 1);

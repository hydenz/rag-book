import type { UIMessage } from "ai";
import type { SourceDto } from "./api";

export const RAG_SOURCES_TYPE = "data-rag-sources" as const;

export type RagDataParts = {
  "rag-sources": { sources: SourceDto[] }; // key has NO "data-" prefix — DataUIPart derives it
};

export type AppUIMessage = UIMessage<unknown, RagDataParts>;

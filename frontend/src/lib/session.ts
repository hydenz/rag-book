// Anonymous per-browser session id — there's no login system. Generated once
// and persisted in localStorage so a page reload keeps the same story/chat
// history scope; sent as X-Session-Id on every API call (see App.tsx and
// ragTransport.ts) so the backend can keep each visitor's story separate.
const STORAGE_KEY = "whispering-archive:session-id";

export function getSessionId(): string {
  let id = localStorage.getItem(STORAGE_KEY);
  if (!id) {
    id = crypto.randomUUID();
    localStorage.setItem(STORAGE_KEY, id);
  }
  return id;
}

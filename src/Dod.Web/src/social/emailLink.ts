export type EmailAction = { action: "verify" | "unsubscribe"; token: string };

export function consumeEmailLink(location: Pick<Location, "hash" | "pathname" | "search">, history: Pick<History, "replaceState">): EmailAction | undefined {
  const match = /^#(verify-email|unsubscribe)=(.*)$/.exec(location.hash);
  if (!match) return;
  let token = "";
  try { token = decodeURIComponent(match[2]); } catch { /* The API will reject malformed links. */ }
  history.replaceState(null, "", location.pathname + location.search);
  return { action: match[1] === "verify-email" ? "verify" : "unsubscribe", token };
}

import { afterEach, describe, expect, it, vi } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { consumeEmailLink } from "./emailLink";
import { EmailLinkAction } from "./EmailSettings";
import { api, resetCsrf } from "../api";

afterEach(() => { vi.unstubAllGlobals(); resetCsrf(); });
describe("email confirmation and unsubscribe links", () => {
  it.each([
    ["#verify-email=abc_DEF-123", "verify", "abc_DEF-123"],
    ["#unsubscribe=protected%2Ftoken%3D", "unsubscribe", "protected/token="],
    ["#verify-email=%zz", "verify", ""],
    ["#unsubscribe=", "unsubscribe", ""],
  ])("consumes %s and removes the bearer token from the address bar", (hash, action, token) => {
    const replaceState = vi.fn();
    expect(consumeEmailLink({ hash, pathname: "/", search: "?lang=en" }, { replaceState })).toEqual({ action, token });
    expect(replaceState).toHaveBeenCalledWith(null, "", "/?lang=en");
  });
  it("leaves unrelated fragments alone", () => {
    const replaceState = vi.fn();
    expect(consumeEmailLink({ hash: "#journal", pathname: "/", search: "" }, { replaceState })).toBeUndefined();
    expect(replaceState).not.toHaveBeenCalled();
  });
  it.each(["verify", "unsubscribe"] as const)("requires an explicit button before %s", action => {
    const fetch = vi.fn(); vi.stubGlobal("fetch", fetch);
    const html = renderToStaticMarkup(<EmailLinkAction action={action} token="private-token" />);
    expect(html).toContain(action === "verify" ? ">Confirm email</button>" : ">Unsubscribe</button>");
    expect(html).not.toContain("private-token");
    expect(fetch).not.toHaveBeenCalled();
  });
  it("posts confirmation through the existing CSRF flow", async () => {
    const fetch = vi.fn().mockResolvedValueOnce(new Response(JSON.stringify({ token: "csrf-token" })))
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    vi.stubGlobal("fetch", fetch);
    await api("/api/email/verify", "POST", { token: "confirmation-token" });
    expect(fetch.mock.calls[0][0]).toBe("/api/account/csrf");
    const [url, init] = fetch.mock.calls[1];
    expect(url).toBe("/api/email/verify"); expect(init.method).toBe("POST");
    expect(init.headers.get("X-CSRF-TOKEN")).toBe("csrf-token");
    expect(JSON.parse(init.body)).toEqual({ token: "confirmation-token" });
  });
});

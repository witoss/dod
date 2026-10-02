let token: string | undefined;
export function resetCsrf() {
  token = undefined;
}
export async function apiFetch(
  url: string,
  init: RequestInit = {},
): Promise<Response> {
  if (init.method && !["GET", "HEAD"].includes(init.method.toUpperCase())) {
    if (!token) {
      const response = await fetch("/api/account/csrf");
      if (!response.ok)
        throw new Error("Could not verify session. Refresh and try again.");
      token = (await response.json()).token;
    }
    const headers = new Headers(init.headers);
    headers.set("X-CSRF-TOKEN", token!);
    init = { ...init, headers };
  }
  const response = await fetch(url, init);
  if (response.status === 401 && !url.startsWith("/api/account/"))
    window.dispatchEvent(new Event("dod-session-expired"));
  return response;
}
export async function api<T = void>(
  url: string,
  method = "GET",
  data?: unknown,
): Promise<T> {
  const response = await apiFetch(url, {
    method,
    ...(data === undefined
      ? {}
      : {
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(data),
        }),
  });
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    throw new Error(
      problem.detail ||
        (response.status === 429
          ? "Too many attempts. Please wait a minute."
          : "Request failed. Please try again."),
    );
  }
  return response.status === 204 ? (undefined as T) : response.json();
}

// @vitest-environment jsdom
import { afterEach, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ForgotPasswordForm, ResetPasswordPage } from "./PasswordReset";
import { AccountGate } from "./Account";
import { resetCsrf } from "../api";

afterEach(() => { cleanup(); resetCsrf(); vi.unstubAllGlobals(); });
const generic = "If this account has a confirmed email address, a password reset link will be sent to it.";
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });

it("does not submit or expose a reset token when the email link is opened", () => {
  const fetch = vi.fn(); vi.stubGlobal("fetch", fetch);
  const { container } = render(<ResetPasswordPage token="private-reset-token" />);
  expect(fetch).not.toHaveBeenCalled();
  expect(container.innerHTML).not.toContain("private-reset-token");
  expect(screen.getByRole("button", { name: "Reset password" })).toBeTruthy();
});

it("requires matching passwords before submitting", async () => {
  const fetch = vi.fn(); vi.stubGlobal("fetch", fetch);
  const user = userEvent.setup(); render(<ResetPasswordPage token="reset-token" />);
  await user.type(screen.getByLabelText("New password"), "new-password-456");
  await user.type(screen.getByLabelText("Confirm new password"), "different-password");
  await user.click(screen.getByRole("button", { name: "Reset password" }));
  expect(screen.getByRole("alert").textContent).toContain("Passwords do not match");
  expect(fetch).not.toHaveBeenCalled();
});

it("submits the token and new password through CSRF and returns to normal sign-in", async () => {
  const fetch = vi.fn().mockResolvedValueOnce(json({ token: "csrf-token" }))
    .mockResolvedValueOnce(new Response(null, { status: 204 }));
  vi.stubGlobal("fetch", fetch);
  const user = userEvent.setup(); render(<ResetPasswordPage token="reset-token" />);
  await user.type(screen.getByLabelText("New password"), "new-password-456");
  await user.type(screen.getByLabelText("Confirm new password"), "new-password-456");
  await user.click(screen.getByRole("button", { name: "Reset password" }));
  expect((await screen.findByRole("status")).textContent).toContain("existing sessions were signed out");
  expect(fetch.mock.calls[0][0]).toBe("/api/account/csrf");
  const [url, request] = fetch.mock.calls[1];
  expect(url).toBe("/api/account/reset-password");
  expect(request.headers.get("X-CSRF-TOKEN")).toBe("csrf-token");
  expect(JSON.parse(request.body)).toEqual({ token: "reset-token", password: "new-password-456" });
  expect(screen.queryByLabelText("New password")).toBeNull();
  expect(fetch).toHaveBeenCalledTimes(2);
});

it("shows an expired-link error without marking the password changed", async () => {
  const fetch = vi.fn().mockResolvedValueOnce(json({ token: "csrf-token" }))
    .mockResolvedValueOnce(json({ detail: "This password reset link is expired. Request a new one." }, 400));
  vi.stubGlobal("fetch", fetch);
  const user = userEvent.setup(); render(<ResetPasswordPage token="expired-token" />);
  await user.type(screen.getByLabelText("New password"), "new-password-456");
  await user.type(screen.getByLabelText("Confirm new password"), "new-password-456");
  await user.click(screen.getByRole("button", { name: "Reset password" }));
  expect((await screen.findByRole("alert")).textContent).toContain("expired");
  expect(screen.queryByRole("status")).toBeNull();
});

it("disables malformed reset links", () => {
  render(<ResetPasswordPage token="" />);
  expect((screen.getByRole("button", { name: "Reset password" }) as HTMLButtonElement).disabled).toBe(true);
  expect(screen.getByRole("alert").textContent).toContain("invalid");
});

it("requests recovery by nickname and displays a generic eligibility response", async () => {
  const fetch = vi.fn().mockResolvedValueOnce(json({ token: "csrf-token" }))
    .mockResolvedValueOnce(json({ detail: generic }));
  vi.stubGlobal("fetch", fetch);
  const user = userEvent.setup(); render(<ForgotPasswordForm />);
  await user.type(screen.getByLabelText("Nickname"), "Alice");
  await user.click(screen.getByRole("button", { name: "Send reset link" }));
  expect((await screen.findByRole("status")).textContent).toContain(generic);
  const [url, request] = fetch.mock.calls[1];
  expect(url).toBe("/api/account/forgot-password");
  expect(JSON.parse(request.body)).toEqual({ nickname: "Alice" });
  expect(request.headers.get("X-CSRF-TOKEN")).toBe("csrf-token");
});

it("makes password recovery reachable from the signed-out sign-in page", async () => {
  const fetch = vi.fn().mockResolvedValueOnce(new Response(null, { status: 401 }));
  vi.stubGlobal("fetch", fetch);
  const user = userEvent.setup(); render(<AccountGate>{() => <p>Signed in</p>}</AccountGate>);
  await user.click(await screen.findByRole("button", { name: "Forgot password?" }));
  expect(screen.getByRole("heading", { name: "Forgot your password?" })).toBeTruthy();
  await user.click(screen.getByRole("button", { name: "Back to sign in" }));
  expect(screen.getByRole("heading", { name: "Welcome back" })).toBeTruthy();
});

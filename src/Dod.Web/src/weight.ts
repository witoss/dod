export function kilogramsLost(
  entries: readonly { date: string; weightKg: number | null }[],
): number | null {
  const weights = entries
    .filter((entry) => entry.weightKg !== null)
    .sort((a, b) => a.date.localeCompare(b.date));
  if (weights.length === 0) return null;
  // Round to measurement precision, including normalizing negative zero.
  return Math.round(
    (weights[0].weightKg! - weights[weights.length - 1].weightKg!) * 100,
  ) / 100 || 0;
}

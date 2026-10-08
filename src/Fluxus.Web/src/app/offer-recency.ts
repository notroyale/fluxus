export const recentOfferDays = 5;

export function isRecentlyAdded(firstSeenAt: string | null, now: number): boolean {
  if (firstSeenAt === null) return false;
  const added = Date.parse(firstSeenAt);
  return (
    Number.isFinite(added) && added <= now && added >= now - recentOfferDays * 24 * 60 * 60 * 1000
  );
}

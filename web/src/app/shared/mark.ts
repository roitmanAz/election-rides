/**
 * Safe HTML of a text with the searched part marked, the same look everywhere (lists, audit log).
 * Every match is marked; letter case is ignored (for emails).
 */
export function markHtml(text: string | null | undefined, query: string | null | undefined): string {
  const esc = (s: string) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!);
  const t = text ?? '';
  const q = (query ?? '').trim();
  if (!q) return esc(t);
  const lowT = t.toLowerCase();
  const lowQ = q.toLowerCase();
  let out = '';
  let from = 0;
  for (let i = lowT.indexOf(lowQ); i >= 0; i = lowT.indexOf(lowQ, from)) {
    out += esc(t.slice(from, i)) + '<mark class="rounded-sm bg-mark px-px font-bold text-ink">' + esc(t.slice(i, i + q.length)) + '</mark>';
    from = i + q.length;
  }
  return out + esc(t.slice(from));
}

/**
 * How well a name matches what was typed: 0 when the name starts with it, 1 when another word in it
 * starts with it, 2 when it is somewhere inside a word, -1 when not at all. Letter case is ignored.
 */
export function matchRank(text: string, query: string): number {
  const t = text.toLowerCase();
  const q = query.trim().toLowerCase();
  if (!q) return 0;
  const i = t.indexOf(q);
  if (i < 0) return -1;
  if (i === 0) return 0;
  return t.split(/[\s\-–־'"]+/).some((w) => w.startsWith(q)) ? 1 : 2;
}

/** Keeps the items that match, those starting with the typed text first; the list's own order stays within each group. */
export function filterRanked<T>(items: readonly T[], query: string, label: (item: T) => string): T[] {
  if (!query.trim()) return [...items];
  return items
    .map((item, index) => ({ item, index, rank: matchRank(label(item), query) }))
    .filter((x) => x.rank >= 0)
    .sort((a, b) => a.rank - b.rank || a.index - b.index)
    .map((x) => x.item);
}

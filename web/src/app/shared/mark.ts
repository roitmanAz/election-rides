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

// TypeScript port of the .NET PolicyPilot.Core text utilities (used by the in-browser demo backend).
const STOP = new Set(('a an the and or of to in on for with is are was were be been by at as it its this that these those from can do does did i my me we our you your what which who how when where will would should could may if than then there their them has have had not no any per about into also so such tell please').split(' '));
const FILLER = new Set('many much long take takes handle handles within should would could need needs get give tell please whom does happen happens'.split(' '));

export function stem(w: string): string {
  if (w.length <= 3 || /^\d/.test(w)) return w;
  for (const suf of ['ations', 'ation', 'ingly', 'ings', 'ing', 'edly', 'ies', 'ied', 'ed', 'es', 's'])
    if (w.endsWith(suf) && w.length - suf.length >= 3) return suf === 'ies' || suf === 'ied' ? w.slice(0, -suf.length) + 'y' : w.slice(0, -suf.length);
  return w;
}
export function tokens(text: string): string[] {
  const res: string[] = [];
  for (const m of text.toLowerCase().matchAll(/[a-z0-9]+(?:\.[0-9]+)?/g)) {
    const w = m[0];
    if (w.length < 2 || STOP.has(w) || FILLER.has(w)) continue;
    res.push(stem(w));
  }
  return res;
}
export const wordCount = (s: string) => s.split(/\s+/).filter(Boolean).length;

export interface DocumentMeta { id: string; title: string; category: string; allowedRoles: string[]; source: string; }
export interface Chunk { id: string; docId: string; docTitle: string; section: string; text: string; allowedRoles: string[]; ordinal: number; }
export const indexText = (c: Chunk) => `${c.docTitle}. ${c.section}. ${c.text}`;

export function parseFrontMatter(defaultId: string, text: string, source: string): { meta: DocumentMeta; body: string } {
  const props: Record<string, string> = {};
  let body = text.replace(/\r\n/g, '\n');
  if (body.startsWith('---\n')) {
    const end = body.indexOf('\n---', 4);
    if (end > 0) {
      for (const line of body.slice(4, end).split('\n')) { const i = line.indexOf(':'); if (i > 0) props[line.slice(0, i).trim().toLowerCase()] = line.slice(i + 1).trim(); }
      body = body.slice(end + 4).replace(/^\n+/, '');
    }
  }
  const roles = (props['roles'] ?? 'customer,officer,admin').split(',').map(s => s.trim()).filter(Boolean);
  const id = props['id'] ?? defaultId;
  return { meta: { id, title: props['title'] ?? id, category: props['category'] ?? 'general', allowedRoles: roles, source }, body };
}

export function chunkDocument(doc: DocumentMeta, text: string, maxWords = 110, overlap = 20): Chunk[] {
  const chunks: Chunk[] = []; let section = doc.title; let buf: string[] = []; let ord = 0;
  const flush = (sec: string, body: string) => {
    const words = body.split(/\s+/).filter(Boolean);
    if (!words.length) return;
    for (let start = 0; start < words.length; start += Math.max(1, maxWords - overlap)) {
      chunks.push({ id: `${doc.id}#${String(ord).padStart(3, '0')}`, docId: doc.id, docTitle: doc.title, section: sec, text: words.slice(start, start + maxWords).join(' '), allowedRoles: doc.allowedRoles, ordinal: ord });
      ord++;
      if (start + maxWords >= words.length) break;
    }
  };
  for (const raw of text.replace(/\r\n/g, '\n').split('\n')) {
    const line = raw.trimEnd();
    if (line.startsWith('#')) { flush(section, buf.join('\n')); buf = []; section = line.replace(/^[# ]+/, '').trim(); } else buf.push(line);
  }
  flush(section, buf.join('\n'));
  return chunks;
}

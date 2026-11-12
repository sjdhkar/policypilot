import { tokens } from './text';

// Query understanding: normalise vocabulary (CIBIL -> credit score) and split compound questions into sub-questions.
const SYN: [RegExp, string][] = [
  [/\bcibil(\s+score)?\b/gi, 'credit score'], [/\bcredit\s+(rating|report)\b/gi, 'credit score'], [/\btenor\b/gi, 'tenure'],
  [/\broi\b/gi, 'interest rate'], [/\bpre-?closure\b/gi, 'foreclosure'], [/\bpre-?payment\s+penalty\b/gi, 'prepayment charge'],
  [/\bdisputed?\s+charges?\b/gi, 'unauthorised transaction'],
];
export const normalise = (q: string) => SYN.reduce((s, [p, r]) => s.replace(p, r), q).trim();
export function decompose(q: string): string[] {
  const parts = q.split(/\s*(?:,|;|&|\band\b|\balso\b|\bplus\b|\bas well as\b)\s*/i).map(p => p.replace(/^[\s?.]+|[\s?.]+$/g, '')).filter(p => tokens(p).length >= 1);
  return parts.length < 2 || parts.length > 4 ? [q] : parts;
}

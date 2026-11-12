import { tokens } from './text';

const INJ: RegExp[] = [
  /ignore\s+(all\s+|any\s+|the\s+)?(previous|prior|above|earlier)\s+(instructions|rules|prompts?)/i,
  /disregard\s+(all\s+|any\s+|the\s+)?(previous|prior|above|earlier|your)\s+(instructions|rules|guidelines)/i,
  /(reveal|show|print|repeat|leak)\s+(me\s+)?(your|the)\s+(system|hidden|initial)\s+(prompt|instructions|message)/i,
  /you\s+are\s+now\s+(a|an|the)?\s*\w+/i,
  /\b(jailbreak|developer\s+mode|DAN\s+mode|do\s+anything\s+now)\b/i,
  /pretend\s+(to\s+be|you\s+are)/i,
  /(bypass|override|disable)\s+(the\s+)?(safety|guardrails?|filters?|restrictions?|access\s+control)/i,
  /act\s+as\s+(an?\s+)?(admin|administrator|root|officer|system)/i,
  /<\/?\s*(system|assistant)\s*>/i,
];
export const injectionDetected = (t: string) => INJ.some(r => r.test(t));

export function luhn(d: string): boolean {
  let sum = 0, alt = false;
  for (let i = d.length - 1; i >= 0; i--) { let n = d.charCodeAt(i) - 48; if (alt) { n *= 2; if (n > 9) n -= 9; } sum += n; alt = !alt; }
  return sum % 10 === 0;
}
export function redactPii(input: string): { text: string; found: string[] } {
  const found: string[] = []; let s = input;
  s = s.replace(/\b(?:\d[ -]?){13,19}\b/g, m => { const d = m.replace(/\D/g, ''); if (d.length < 13 || d.length > 19 || !luhn(d)) return m; found.push('CARD'); return '[CARD]'; });
  const rep = (re: RegExp, tok: string, label: string) => { if (re.test(s)) { found.push(label); s = s.replace(re, tok); } };
  rep(/[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g, '[EMAIL]', 'EMAIL');
  rep(/\b[A-Z]{5}[0-9]{4}[A-Z]\b/g, '[PAN]', 'PAN');
  rep(/\b[2-9][0-9]{3}\s?[0-9]{4}\s?[0-9]{4}\b/g, '[AADHAAR]', 'AADHAAR');
  rep(/(?<!\d)(?:\+91[\s-]?)?[6-9][0-9]{9}(?!\d)/g, '[PHONE]', 'PHONE');
  return { text: s, found: [...new Set(found)] };
}

export function checkOutput(answer: string, ctxTexts: string[], threshold = 0.5) {
  const cited = [...new Set([...answer.matchAll(/\[(\d+)\]/g)].map(m => +m[1]))];
  const invalid = cited.filter(c => c < 1 || c > ctxTexts.length);
  const ctx = new Set(ctxTexts.flatMap(tokens));
  const sentences = answer.replace(/\[(\d+)\]/g, '').split(/(?<=[.!?])\s+|\n+/).map(s => s.trim()).filter(s => tokens(s).length >= 3);
  const ungrounded = sentences.filter(s => { const t = tokens(s); return t.filter(x => ctx.has(x)).length / t.length < threshold; });
  return { citationsValid: invalid.length === 0, groundedRatio: sentences.length === 0 ? 1 : 1 - ungrounded.length / sentences.length, invalid };
}

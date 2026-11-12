import { stem, tokens } from './text';

// Query understanding: normalise vocabulary (CIBIL -> credit score) and split compound questions into sub-questions.
const SYN: [RegExp, string][] = [
  [/\bcibil(\s+score)?\b/gi, 'credit score'], [/\bcredit\s+(rating|report)\b/gi, 'credit score'], [/\btenor\b/gi, 'tenure'],
  [/\broi\b/gi, 'interest rate'], [/\bpre-?closure\b/gi, 'foreclosure'], [/\bpre-?payment\s+penalty\b/gi, 'prepayment charge'],
  [/\bdisputed?\s+charges?\b/gi, 'unauthorised transaction'],
  // customer intents in everyday language -> the wording used in the policy documents
  [/\b(discontinue|cancel|close|deactivate|terminate|stop|destroy|hotlist)\s+(my\s+|the\s+|that\s+|this\s+|a\s+)?(credit\s+|debit\s+|atm\s+)?card\b/gi, 'block card'],
  [/\bcard\s+(?:(?:does|do|did)(?:\s*n[o']?t|nt)?|doe?nt|is\s*n[o']?t|isn'?t|not|stopped|has\s+stopped|won'?t|wont)\s+work(?:ing)?\b/gi, 'card block replacement card'],
  [/\b(damaged|broken|faulty|expired|cracked)\s+(credit\s+|debit\s+|atm\s+)?card\b/gi, 'block card replacement card'],
  [/\b(card\s+(got\s+|was\s+|is\s+|has\s+been\s+)?(lost|stolen|misplaced)|(lost|stolen|misplaced)\s+(my\s+)?(credit\s+|debit\s+|atm\s+)?card)\b/gi, 'lost or stolen card block'],
  [/\b(without\s+my\s+(permission|knowledge|consent|approval)|someone\s+(used|has\s+used|stole|misused)|fraudulent\s+(transaction|charge|debit)|unknown\s+(transaction|charge|debit)|money\s+(was\s+)?(deducted|debited|stolen))\b/gi, 'unauthorised transaction report'],
  [/\b(pay|repay|settle|clear|close)\s+(off\s+)?(my\s+|the\s+)?(home\s+)?loan\s*(early|before|in\s+advance|fully|completely|now)?\b/gi, 'loan prepayment foreclosure'],
  [/\bpenalt(y|ies)\b/gi, 'charge'],
  [/\bhow\s+much\s+(can|will|could)\s+i\s+(borrow|get)\b/gi, 'maximum loan amount'],
  [/\b(not\s+happy|unhappy|dissatisfied|escalate|file\s+a\s+complaint|raise\s+a\s+complaint)\b/gi, 'grievance complaint escalation'],
];
export const normalise = (q: string) => SYN.reduce((s, [p, r]) => s.replace(p, r), q).trim();
export function decompose(q: string): string[] {
  const parts = q.split(/\s*(?:,|;|&|\band\b|\balso\b|\bplus\b|\bas well as\b)\s*/i).map(p => p.replace(/^[\s?.]+|[\s?.]+$/g, '')).filter(p => tokens(p).length >= 1);
  return parts.length < 2 || parts.length > 4 ? [q] : parts;
}

/** Optimal string alignment distance (insert, delete, substitute, transpose). */
export function distance(a: string, b: string): number {
  const d: number[][] = Array.from({ length: a.length + 1 }, (_, i) => [i, ...Array(b.length).fill(0)]);
  for (let j = 0; j <= b.length; j++) d[0][j] = j;
  for (let i = 1; i <= a.length; i++) for (let j = 1; j <= b.length; j++) {
    d[i][j] = Math.min(d[i - 1][j] + 1, d[i][j - 1] + 1, d[i - 1][j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1));
    if (i > 1 && j > 1 && a[i - 1] === b[j - 2] && a[i - 2] === b[j - 1]) d[i][j] = Math.min(d[i][j], d[i - 2][j - 2] + 1);
  }
  return d[a.length][b.length];
}

/** Typo tolerance: an unknown word becomes the closest vocabulary word if the match is unambiguous (acronyms and common words untouched). */
export function correct(q: string, vocab: Set<string>): string {
  const stems = new Set([...vocab].map(stem));
  return q.replace(/[A-Za-z]{4,}/g, w => {
    if (w === w.toUpperCase() || tokens(w).length === 0) return w;
    const lw = w.toLowerCase(); if (stems.has(stem(lw))) return w;
    const max = lw.length >= 8 ? 2 : 1;
    const cands = [...vocab].filter(v => Math.abs(v.length - lw.length) <= max).map(v => ({ v, d: distance(lw, v) })).filter(x => x.d <= max);
    if (!cands.length) return w;
    const best = Math.min(...cands.map(c => c.d)); const top = cands.filter(c => c.d === best);
    return new Set(top.map(c => stem(c.v))).size === 1 ? top[0].v : w;
  });
}

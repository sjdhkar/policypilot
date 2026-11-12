import { Chunk, DocumentMeta, chunkDocument, indexText, parseFrontMatter, tokens } from './text';
import { checkOutput, injectionDetected, redactPii } from './guardrails';
import { Bm25, Scored, VectorIndex, embed, hybridRetrieve, hybridRetrieveMulti } from './retrieval';
import { correct, decompose, normalise } from './planner';

export interface Citation { index: number; docId: string; docTitle: string; section: string; snippet: string; score: number; }
export interface RagAnswer { answer: string; citations: Citation[]; confidence: number; answered: boolean; flags: string[]; timings: { stage: string; ms: number }[]; }
export interface User { userId: string; role: string; }
export const NO_ANSWER = 'I could not find this in the policy documents I can access.';

/** Knowledge base: documents, chunks, both indexes. */
export class KnowledgeBase {
  docs = new Map<string, DocumentMeta>(); chunks = new Map<string, Chunk>();
  bm25 = new Bm25(); vectors = new VectorIndex();
  ingest(meta: DocumentMeta, body: string) {
    this.remove(meta.id, false);
    this.docs.set(meta.id, meta);
    for (const c of chunkDocument(meta, body)) this.chunks.set(c.id, c);
    this.rebuild(); return [...this.chunks.values()].filter(c => c.docId === meta.id).length;
  }
  remove(id: string, rebuild = true) {
    const had = this.docs.delete(id);
    for (const [k, c] of this.chunks) if (c.docId === id) this.chunks.delete(k);
    if (rebuild && had) this.rebuild(); return had;
  }
  vocabularyFor(role: string): Set<string> {
    const v = new Set<string>();
    for (const c of this.chunks.values()) if (c.allowedRoles.some(r => r.toLowerCase() === role.toLowerCase())) for (const m of indexText(c).toLowerCase().matchAll(/[a-z]{4,}/g)) v.add(m[0]);
    return v;
  }
  private rebuild() {
    const all = [...this.chunks.values()]; this.bm25.build(all); this.vectors.clear();
    for (const c of all) this.vectors.add(c, embed(indexText(c)));
  }
}

// ---------- Mock (extractive) LLM: same behaviour as the .NET MockLlm ----------
function extractAnswer(question: string, ctx: { idx: number; section: string; text: string }[]): string {
  const cand: { idx: number; s: string; set: Set<string> }[] = [];
  for (const c of ctx) for (const s of c.text.split(/(?<=[.!?])\s+/)) {
    const st = tokens(s); if (st.length < 3) continue;
    cand.push({ idx: c.idx, s: s.trim(), set: new Set([...st, ...tokens(c.section)]) });
  }
  const parts = decompose(question); const picked: { idx: number; s: string }[] = [];
  for (const part of parts) {
    const qt = [...new Set(tokens(part))]; if (!qt.length) continue;
    const scored = cand.map(c => ({ idx: c.idx, s: c.s, score: qt.filter(t => c.set.has(t)).length / qt.length })).filter(c => c.score >= 0.2)
      .sort((a, b) => b.score - a.score || a.idx - b.idx);
    const best = parts.length > 1 ? scored.filter(c => c.score >= 0.75 * scored[0].score).slice(0, 2) : scored.filter(c => c.score >= 0.5 * scored[0].score).slice(0, 3);
    for (const b of best) if (!picked.some(p => p.idx === b.idx && p.s === b.s)) picked.push({ idx: b.idx, s: b.s });
  }
  if (!picked.length) return 'I could not find this in the policy documents.';
  return picked.sort((a, b) => a.idx - b.idx).map(p => `${p.s.replace(/\s+$/, '')} [${p.idx}]`).join(' ');
}

export interface RagMetrics { requests: number; answered: number; noAnswer: number; guardrailTrips: number; promptTokens: number; completionTokens: number; latencies: number[]; }
export const newMetrics = (): RagMetrics => ({ requests: 0, answered: 0, noAnswer: 0, guardrailTrips: 0, promptTokens: 0, completionTokens: 0, latencies: [] });

const contextualise = (q: string, history: { role: string; content: string }[]) => {
  const prev = [...history].reverse().find(h => h.role === 'user')?.content; if (!prev) return q;
  const n = tokens(q).length; const follow = n <= 3 || (/\b(it|that|this|those|they|them|its|same)\b/i.test(q) && n <= 4);
  return follow ? `${prev} ${q}` : q;
};
const coverage = (q: string, text: string) => { const qs = [...new Set(tokens(q))]; if (!qs.length) return 0; const set = new Set(tokens(text)); return qs.filter(t => set.has(t)).length / qs.length; };

export function answerQuestion(kb: KnowledgeBase, question: string, user: User, history: { role: string; content: string }[] = [], m?: RagMetrics): RagAnswer {
  const t0 = performance.now(); let lap = t0; const timings: { stage: string; ms: number }[] = []; const flags: string[] = [];
  const mark = (s: string) => { const n = performance.now(); timings.push({ stage: s, ms: Math.round((n - lap) * 100) / 100 }); lap = n; };
  const done = (a: RagAnswer) => { if (m) { m.requests++; a.answered ? m.answered++ : m.noAnswer++; m.latencies.push(performance.now() - t0); } return a; };

  if (injectionDetected(question)) { if (m) m.guardrailTrips++; return done({ answer: "I can't help with that request. Please ask a question about the bank's policies.", citations: [], confidence: 0, answered: false, flags: ['prompt_injection_blocked'], timings }); }
  let { text: safe, found } = redactPii(question); safe = correct(normalise(safe), kb.vocabularyFor(user.role)); if (found.length) { flags.push('pii_redacted:' + found.join(',')); if (m) m.guardrailTrips++; }
  mark('input_guardrails');
  let query = contextualise(safe, history); mark('contextualise');
  const visible = (c: Chunk) => c.allowedRoles.some(r => r.toLowerCase() === user.role.toLowerCase());
  const blocked = (text: string) => {
    const qt = [...new Set(tokens(text))];
    const unk = qt.length ? qt.filter(t => !kb.bm25.contains(t, visible)).length / qt.length : 1;
    const acr = [...text.matchAll(/\b[A-Z]{3,6}\b/g)].some(mm => { const t = tokens(mm[0]); return t.length > 0 && !kb.bm25.contains(t[0], visible); });
    const restr = qt.some(t => !kb.bm25.contains(t, visible) && kb.bm25.contains(t, () => true));
    return unk >= 0.5 || acr || restr;
  };
  let unanswered: string[] = []; const allParts = decompose(query);
  if (allParts.length > 1) {
    const ok = allParts.filter(p => !blocked(p));
    if (ok.length > 0 && ok.length < allParts.length) { unanswered = allParts.filter(p => !ok.includes(p)); query = ok.join(' and '); safe = query; flags.push('partial_answer'); }
  }
  const hits = hybridRetrieveMulti(kb.bm25, kb.vectors, query, user.role); mark('retrieve');

  const clean: Scored[] = [];
  for (const h of hits) {
    if (!injectionDetected(h.chunk.text)) { clean.push(h); continue; }
    flags.push('indirect_injection_sanitised');
    const kept = h.chunk.text.split(/(?<=[.!?])\s+|\n/).filter(l => !injectionDetected(l)).join(' ').trim();
    if (kept) clean.push({ ...h, chunk: { ...h.chunk, text: kept } });
  }
  const gate = blocked(query);
  const parts = decompose(query);
  const cov = !clean.length ? 0 : parts.length < 2 ? coverage(query, indexText(clean[0].chunk)) : parts.reduce((a, p) => a + Math.max(...clean.map(c => coverage(p, indexText(c.chunk)))), 0) / parts.length;
  const confidence = clean.length ? Math.round(Math.min(1, 0.6 * cov + 0.4 * clean[0].score) * 1000) / 1000 : 0;
  if (!clean.length || cov < 0.34 || gate) { mark('confidence_gate'); return done({ answer: NO_ANSWER, citations: [], confidence, answered: false, flags: [...flags, gate ? 'unknown_terms' : 'low_retrieval_confidence'], timings }); }
  mark('confidence_gate');

  let answer = extractAnswer(safe, clean.map((c, i) => ({ idx: i + 1, section: c.chunk.section, text: c.chunk.text }))); mark('generate');
  if (m) { m.promptTokens += Math.ceil((query.length + clean.reduce((a, c) => a + c.chunk.text.length, 0)) / 4); m.completionTokens += Math.ceil(answer.length / 4); }
  answer = redactPii(answer).text;
  const chk = checkOutput(answer, clean.map(c => c.chunk.text));
  if (!chk.citationsValid) { flags.push('invalid_citation'); answer = answer.replace(/\[(\d+)\]/g, (mm, n) => chk.invalid.includes(+n) ? '' : mm); }
  const refused = /could not find/i.test(answer);
  if (!refused && chk.groundedRatio < 0.6) { flags.push(`low_groundedness:${chk.groundedRatio.toFixed(2)}`); mark('output_guardrails'); return done({ answer: NO_ANSWER, citations: [], confidence, answered: false, flags, timings }); }
  mark('output_guardrails');
  const citations = clean.map((c, i) => ({ index: i + 1, docId: c.chunk.docId, docTitle: c.chunk.docTitle, section: c.chunk.section, snippet: c.chunk.text.length <= 220 ? c.chunk.text : c.chunk.text.slice(0, 220) + '…', score: Math.round(c.score * 1000) / 1000 }));
  if (unanswered.length && !refused) answer = answer.trim() + ` Note: I could not find information about "${unanswered.join('" and "')}" in the policy documents I can access.`;
  return done({ answer: answer.trim(), citations, confidence, answered: !refused, flags, timings });
}

// ---------- Agent ----------
export interface ToolStep { tool: string; args: string; result: string; error: boolean; }
export interface AgentResult { answer: string; steps: ToolStep[]; completed: boolean; }
const emi = (p: number, rate: number, n: number) => { if (p <= 0 || n <= 0) throw new Error('principal and tenure must be positive'); const r = rate / 1200; if (r === 0) return p / n; const x = Math.pow(1 + r, n); return p * r * x / (x - 1); };
const fmt = (n: number) => Math.round(n).toLocaleString('en-US');

export function runAgent(kb: KnowledgeBase, question: string, user: User): AgentResult {
  if (injectionDetected(question)) return { answer: "I can't help with that request.", steps: [], completed: false };
  const q = redactPii(question).text; const lower = q.toLowerCase();
  const nums = [...q.matchAll(/-?\d+(?:,\d{3})*(?:\.\d+)?/g)].map(m => parseFloat(m[0].replace(/,/g, '')));
  const steps: ToolStep[] = []; const step = (tool: string, args: object, fn: () => string) => {
    let result: string, error = false; try { result = fn(); } catch (e: any) { result = 'tool error: ' + e.message; error = true; }
    steps.push({ tool, args: JSON.stringify(args), result, error }); return result;
  };
  let result: string;
  if (lower.includes('eligib') && nums.length >= 3) {
    const [income, existing, amount] = nums; const tenure = nums[3] ?? 240; const score = nums[4] ?? 750;
    result = step('loan_eligibility', { monthlyIncome: income, existingEmi: existing, requestedAmount: amount, tenureMonths: tenure, creditScore: score }, () => {
      const e = emi(amount, 10.5, tenure), foir = (existing + e) / income, why: string[] = [];
      if (score < 700) why.push(`credit score ${score} is below the minimum 700`);
      if (foir > 0.5) why.push(`FOIR ${Math.round(foir * 100)}% exceeds the 50% limit`);
      return why.length ? `Indicatively NOT eligible: ${why.join('; ')}. New EMI would be ${fmt(e)}.` : `Indicatively ELIGIBLE. New EMI ${fmt(e)} at 10.5% gives FOIR ${Math.round(foir * 100)}% (limit 50%), credit score ${score} meets the minimum.`;
    });
  } else if (lower.includes('emi') && nums.length >= 3) {
    const [p, r, n] = nums;
    result = step('emi_calculator', { principal: p, annualRatePct: r, tenureMonths: n }, () => { const e = emi(p, r, n); return `EMI is ${fmt(e)} per month; total repayment ${fmt(e * n)}; total interest ${fmt(e * n - p)}.`; });
  } else {
    result = step('policy_search', { query: q }, () => {
      const hits = hybridRetrieve(kb.bm25, kb.vectors, q, user.role, { cand: 20, pool: 8, k: 3, rrf: 60 });
      return hits.length ? hits.map(h => `(${h.chunk.docTitle} > ${h.chunk.section}) ${h.chunk.text}`).join(' | ') : 'No relevant policy found.';
    });
  }
  return { answer: 'Result: ' + result, steps, completed: true };
}

export async function loadKnowledgeBase(base = 'docs/'): Promise<KnowledgeBase> {
  const kb = new KnowledgeBase();
  const files: string[] = await (await fetch(base + 'manifest.json')).json();
  for (const f of files) { const txt = await (await fetch(base + f)).text(); const { meta, body } = parseFrontMatter(f.replace(/\.md$/, ''), txt, f); kb.ingest(meta, body); }
  return kb;
}

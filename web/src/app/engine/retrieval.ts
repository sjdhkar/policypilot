import { Chunk, indexText, tokens } from './text';
import { decompose } from './planner';

export interface Scored { chunk: Chunk; score: number; }
type Allow = (c: Chunk) => boolean;

export class Bm25 {
  private docs: { chunk: Chunk; tf: Map<string, number>; len: number }[] = [];
  private df = new Map<string, number>(); private avg = 0;
  constructor(private k1 = 1.5, private b = 0.75) {}
  build(chunks: Chunk[]) {
    this.docs = []; this.df.clear();
    for (const c of chunks) {
      const toks = tokens(indexText(c)); const tf = new Map<string, number>();
      for (const t of toks) tf.set(t, (tf.get(t) ?? 0) + 1);
      for (const t of tf.keys()) this.df.set(t, (this.df.get(t) ?? 0) + 1);
      this.docs.push({ chunk: c, tf, len: toks.length });
    }
    this.avg = this.docs.length ? this.docs.reduce((a, d) => a + d.len, 0) / this.docs.length : 0;
  }
  contains(term: string, allow: Allow) { return this.docs.some(d => d.tf.has(term) && allow(d.chunk)); }
  search(query: string, topK: number, allow: Allow): Scored[] {
    const q = [...new Set(tokens(query))]; const n = this.docs.length; const res: Scored[] = [];
    for (const d of this.docs) {
      if (!allow(d.chunk)) continue;
      let s = 0;
      for (const t of q) {
        const f = d.tf.get(t); if (!f) continue;
        const df = this.df.get(t)!; const idf = Math.log(1 + (n - df + 0.5) / (df + 0.5));
        s += idf * (f * (this.k1 + 1)) / (f + this.k1 * (1 - this.b + this.b * d.len / Math.max(1, this.avg)));
      }
      if (s > 0) res.push({ chunk: d.chunk, score: s });
    }
    return res.sort((a, b) => b.score - a.score).slice(0, topK);
  }
}

/** Feature-hashing embedding (deterministic, offline). FNV-1a replaces MD5 used in the .NET build; the scheme is identical. */
export function embed(text: string, dim = 768): Float32Array {
  const v = new Float32Array(dim); const toks = tokens(text);
  const add = (f: string, w: number) => {
    let h = 0x811c9dc5; for (let i = 0; i < f.length; i++) { h ^= f.charCodeAt(i); h = Math.imul(h, 0x01000193) >>> 0; }
    v[h % dim] += ((h >>> 16) & 1) === 0 ? w : -w;
  };
  toks.forEach(t => add(t, 1)); for (let i = 0; i + 1 < toks.length; i++) add(toks[i] + '_' + toks[i + 1], 0.7);
  const n = Math.sqrt(v.reduce((a, x) => a + x * x, 0)); if (n > 0) for (let i = 0; i < dim; i++) v[i] /= n;
  return v;
}

export class VectorIndex {
  items: { chunk: Chunk; vec: Float32Array }[] = [];
  clear() { this.items = []; } add(chunk: Chunk, vec: Float32Array) { this.items.push({ chunk, vec }); }
  search(q: Float32Array, topK: number, allow: Allow): Scored[] {
    return this.items.filter(i => allow(i.chunk)).map(i => { let s = 0; for (let k = 0; k < q.length; k++) s += q[k] * i.vec[k]; return { chunk: i.chunk, score: s }; })
      .filter(r => r.score > 0).sort((a, b) => b.score - a.score).slice(0, topK);
  }
}

const bigrams = (t: string[]) => t.slice(0, -1).map((x, i) => x + ' ' + t[i + 1]);

export function rerank(query: string, pool: Scored[]): Scored[] {
  const q = [...new Set(tokens(query))]; if (!q.length || !pool.length) return pool;
  const qb = new Set(bigrams(tokens(query))); const maxRrf = Math.max(...pool.map(p => p.score));
  return pool.map(p => {
    const toks = tokens(indexText(p.chunk)); const set = new Set(toks);
    const coverage = q.filter(t => set.has(t)).length / q.length;
    const phrase = qb.size === 0 ? 0 : bigrams(toks).filter(b => qb.has(b)).length / qb.size;
    const sec = new Set(tokens(p.chunk.section)); const secHit = q.filter(t => sec.has(t)).length / q.length;
    return { ...p, score: 0.45 * (p.score / maxRrf) + 0.30 * coverage + 0.15 * Math.min(1, phrase * 2) + 0.10 * secHit };
  }).sort((a, b) => b.score - a.score);
}

export function hybridRetrieve(bm25: Bm25, vectors: VectorIndex, query: string, role: string, o = { cand: 20, pool: 8, k: 4, rrf: 60 }): Scored[] {
  const allow: Allow = c => c.allowedRoles.some(r => r.toLowerCase() === role.toLowerCase());
  const lex = bm25.search(query, o.cand, allow); const dense = vectors.search(embed(query), o.cand, allow);
  const fused = new Map<string, Scored>();
  lex.forEach((r, i) => fused.set(r.chunk.id, { chunk: r.chunk, score: 1 / (o.rrf + i + 1) }));
  dense.forEach((r, i) => { const e = fused.get(r.chunk.id); fused.set(r.chunk.id, { chunk: r.chunk, score: (e?.score ?? 0) + 1 / (o.rrf + i + 1) }); });
  const pool = [...fused.values()].sort((a, b) => b.score - a.score).slice(0, o.pool);
  return rerank(query, pool).slice(0, o.k);
}

/** Multi-topic retrieval: each sub-question is retrieved separately, interleaved round-robin, then topped up with the whole-question ranking. */
export function hybridRetrieveMulti(bm25: Bm25, vectors: VectorIndex, query: string, role: string, o = { cand: 20, pool: 8, k: 4, rrf: 60 }): Scored[] {
  const parts = decompose(query); const whole = hybridRetrieve(bm25, vectors, query, role, o);
  if (parts.length < 2) return whole;
  const per = parts.map(p => hybridRetrieve(bm25, vectors, p, role, { ...o, k: 2 }));
  const merged: Scored[] = []; const seen = new Set<string>(); const add = (c: Scored) => { if (!seen.has(c.chunk.id)) { seen.add(c.chunk.id); merged.push(c); } };
  for (let r = 0; r < 2; r++) per.forEach(l => { if (r < l.length) add(l[r]); });
  whole.forEach(add);
  return merged.slice(0, o.k + 2);
}

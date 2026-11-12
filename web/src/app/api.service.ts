import { Injectable, signal } from '@angular/core';
import { HttpClient, HttpHeaders } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { KnowledgeBase, RagMetrics, answerQuestion, loadKnowledgeBase, newMetrics, runAgent } from './engine/pipeline';
import { parseFrontMatter } from './engine/text';

export interface Citation { index: number; docId: string; docTitle: string; section: string; snippet: string; score: number; }
export interface Timing { stage: string; ms: number; }
export interface Meta { citations: Citation[]; confidence: number; answered: boolean; flags: string[]; timings: Timing[]; }
export interface Turn { role: 'user' | 'assistant'; content: string; meta?: Meta; streaming?: boolean; }
export interface ToolStep { tool: string; args: string; result: string; error: boolean; }
export interface AgentResult { answer: string; steps: ToolStep[]; completed: boolean; }
export interface DocInfo { id: string; title: string; category: string; allowedRoles: string[]; }

export const API: string = (window as any).API_URL ?? 'http://localhost:5080';
/** 'local' = the whole RAG backend runs in the browser (GitHub Pages demo); otherwise the UI calls the .NET API. */
export const LOCAL = API === 'local';
const DEMO_USERS: Record<string, { password: string; role: string }> = {
  customer: { password: 'customer123', role: 'customer' }, officer: { password: 'officer123', role: 'officer' }, admin: { password: 'admin123', role: 'admin' } };
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));

@Injectable({ providedIn: 'root' })
export class ApiService {
  token = signal<string | null>(sessionStorage.getItem('pp_token'));
  role = signal<string>(sessionStorage.getItem('pp_role') ?? 'anonymous');
  user = signal<string>(sessionStorage.getItem('pp_user') ?? '');
  local = LOCAL;
  private kb?: KnowledgeBase; private metricsState: RagMetrics = newMetrics(); private hits: number[] = [];
  constructor(private http: HttpClient) {}

  private headers() { const t = this.token(); return new HttpHeaders(t ? { Authorization: `Bearer ${t}` } : {}); }
  private async kbase() { return (this.kb ??= await loadKnowledgeBase()); }
  private ctx() { return { userId: this.user() || 'anonymous', role: this.role() === 'anonymous' ? 'customer' : this.role() }; }
  private limited() { const now = Date.now(); this.hits = this.hits.filter(t => now - t < 60000); if (this.hits.length >= 30) return true; this.hits.push(now); return false; }

  async login(username: string, password: string) {
    let r: { token: string; role: string; username: string };
    if (LOCAL) {
      const u = DEMO_USERS[username.toLowerCase()]; if (!u || u.password !== password) throw new Error('invalid');
      r = { token: 'local-demo-token', role: u.role, username: username.toLowerCase() };
    } else r = await firstValueFrom(this.http.post<{ token: string; role: string; username: string }>(`${API}/api/auth/login`, { username, password }));
    this.token.set(r.token); this.role.set(r.role); this.user.set(r.username);
    sessionStorage.setItem('pp_token', r.token); sessionStorage.setItem('pp_role', r.role); sessionStorage.setItem('pp_user', r.username);
  }
  logout() {
    this.token.set(null); this.role.set('anonymous'); this.user.set('');
    ['pp_token', 'pp_role', 'pp_user'].forEach(k => sessionStorage.removeItem(k));
  }

  /** Streams an answer. Local mode runs the TypeScript engine; otherwise SSE from the .NET API (fetch, because EventSource cannot POST or send auth headers). */
  async chatStream(question: string, history: { role: string; content: string }[], onMeta: (m: Meta) => void, onToken: (t: string) => void) {
    if (LOCAL) {
      if (this.limited()) throw new Error('Rate limit reached. Please wait a minute.');
      const a = answerQuestion(await this.kbase(), question, this.ctx(), history, this.metricsState);
      onMeta({ citations: a.citations, confidence: a.confidence, answered: a.answered, flags: a.flags, timings: a.timings });
      for (const piece of a.answer.split(/(?<=\s)/).filter(Boolean)) { onToken(piece); await sleep(12); }
      return;
    }
    const res = await fetch(`${API}/api/chat/stream`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...(this.token() ? { Authorization: `Bearer ${this.token()}` } : {}) },
      body: JSON.stringify({ question, history }),
    });
    if (res.status === 429) throw new Error('Rate limit reached. Please wait a minute.');
    if (!res.ok || !res.body) throw new Error(`Request failed (${res.status})`);
    const reader = res.body.getReader(); const dec = new TextDecoder(); let buf = '';
    for (;;) {
      const { value, done } = await reader.read(); if (done) break;
      buf += dec.decode(value, { stream: true });
      let i: number;
      while ((i = buf.indexOf('\n\n')) >= 0) {
        const block = buf.slice(0, i); buf = buf.slice(i + 2);
        const ev = /event: (.*)/.exec(block)?.[1]; const data = /data: (.*)/.exec(block)?.[1];
        if (!ev || !data) continue;
        const j = JSON.parse(data);
        if (ev === 'meta') onMeta(j as Meta); else if (ev === 'token') onToken(j.text);
      }
    }
  }

  async agent(question: string): Promise<AgentResult> {
    if (LOCAL) { if (this.limited()) throw { status: 429 }; return runAgent(await this.kbase(), question, this.ctx()); }
    return firstValueFrom(this.http.post<AgentResult>(`${API}/api/agent`, { question }, { headers: this.headers() }));
  }
  async documents(): Promise<DocInfo[]> {
    if (LOCAL) { const role = this.ctx().role; return [...(await this.kbase()).docs.values()].filter(d => d.allowedRoles.includes(role)).sort((a, b) => a.title.localeCompare(b.title)).map(d => ({ id: d.id, title: d.title, category: d.category, allowedRoles: d.allowedRoles })); }
    return firstValueFrom(this.http.get<DocInfo[]>(`${API}/api/documents`, { headers: this.headers() }));
  }
  async upload(file: File, roles: string) {
    if (LOCAL) {
      if (this.role() !== 'admin') throw { status: 403 };
      if (!/\.(md|txt)$/i.test(file.name) || file.size > 2_000_000) throw { status: 400 };
      const id = file.name.replace(/\.[^.]+$/, '').toLowerCase().replace(/ /g, '-');
      const { meta, body } = parseFrontMatter(id, await file.text(), file.name);
      if (roles.trim()) meta.allowedRoles = roles.split(',').map(s => s.trim()).filter(Boolean);
      return { id: meta.id, chunks: (await this.kbase()).ingest(meta, body) };
    }
    const f = new FormData(); f.append('file', file); if (roles) f.append('roles', roles);
    return firstValueFrom(this.http.post<{ id: string; chunks: number }>(`${API}/api/documents`, f, { headers: this.headers() }));
  }
  async remove(id: string) {
    if (LOCAL) { if (this.role() !== 'admin') throw { status: 403 }; (await this.kbase()).remove(id); return; }
    await firstValueFrom(this.http.delete(`${API}/api/documents/${id}`, { headers: this.headers() }));
  }
  async metrics() {
    if (LOCAL) {
      if (this.role() === 'anonymous' || this.role() === 'customer') throw { status: 403 };
      const m = this.metricsState, s = [...m.latencies].sort((a, b) => a - b), p = (q: number) => s.length ? Math.round(s[Math.min(s.length - 1, Math.floor(q * s.length))] * 10) / 10 : 0;
      return { requests: m.requests, answered: m.answered, noAnswer: m.noAnswer, guardrailTrips: m.guardrailTrips, promptTokens: m.promptTokens, completionTokens: m.completionTokens, p50Ms: p(0.5), p95Ms: p(0.95) };
    }
    return firstValueFrom(this.http.get<any>(`${API}/api/metrics`, { headers: this.headers() }));
  }
}

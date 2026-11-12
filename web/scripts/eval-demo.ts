// Runs the same golden set as the .NET evaluation against the in-browser (TypeScript) engine. Usage: npm run eval:demo
import * as fs from 'fs'; import * as path from 'path';
import { KnowledgeBase, answerQuestion, runAgent } from '../src/app/engine/pipeline';
import { parseFrontMatter } from '../src/app/engine/text';
import { hybridRetrieve } from '../src/app/engine/retrieval';

const root = path.join(__dirname, '..', '..');
const kb = new KnowledgeBase();
for (const f of fs.readdirSync(path.join(root, 'data', 'docs')).filter(f => f.endsWith('.md')).sort()) {
  const { meta, body } = parseFrontMatter(f.replace(/\.md$/, ''), fs.readFileSync(path.join(root, 'data', 'docs', f), 'utf8'), f); kb.ingest(meta, body);
}
const golden = JSON.parse(fs.readFileSync(path.join(root, 'data', 'eval', 'golden.json'), 'utf8'));
let fail = 0; let answerable = 0, hit1 = 0, hitK = 0, cont = 0, cited = 0, refCases = 0, refOk = 0, aclCases = 0, aclOk = 0;
for (const g of golden) {
  const user = { userId: 'eval', role: g.role }; const a = answerQuestion(kb, g.question, user); let ok: boolean;
  if (g.expectRefusal) { refCases++; ok = !a.answered; if (ok) refOk++; if (g.forbidDoc) { aclCases++; if (ok && !a.citations.some(c => c.docId === g.forbidDoc)) aclOk++; } }
  else {
    answerable++; const hits = hybridRetrieve(kb.bm25, kb.vectors, g.question, g.role); const rank = hits.findIndex(h => h.chunk.docId === g.expectDoc) + 1;
    if (rank === 1) hit1++; if (rank > 0) hitK++;
    const text = (g.expectContains ?? []).every((s: string) => a.answer.toLowerCase().includes(s.toLowerCase())); if (text && a.answered) cont++;
    const cite = a.citations.some(c => c.docId === g.expectDoc); if (cite) cited++;
    ok = rank > 0 && text && a.answered && cite;
  }
  if (!ok) { fail++; console.log('FAIL', g.id, g.question, '->', a.answer.slice(0, 80), a.flags); }
}
const p = (a: number, b: number) => (b ? Math.round(100 * a / b) : 100) + '%';
console.log(`cases ${golden.length}, failed ${fail} | hit@1 ${p(hit1, answerable)} hit@4 ${p(hitK, answerable)} facts ${p(cont, answerable)} cite ${p(cited, answerable)} refusals ${p(refOk, refCases)} acl ${p(aclOk, aclCases)}`);
// agent parity with .NET
const u = { userId: 'a', role: 'customer' };
const e1 = runAgent(kb, 'Calculate EMI for a loan of 2500000 at 9 percent for 180 months', u).answer;
const e2 = runAgent(kb, 'Check eligibility income 100000 existing emi 10000 amount 5000000 tenure 240 score 650', u).answer;
console.log(e1); console.log(e2);
if (!e1.includes('25,357') || !e2.includes('NOT eligible')) { console.log('AGENT FAIL'); fail++; }
process.exit(fail ? 1 : 0);

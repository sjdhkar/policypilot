# PolicyPilot - RAG assistant for bank policy & compliance (.NET 8 + Angular)

An end-to-end Retrieval-Augmented Generation system: ingestion, hybrid retrieval, reranking, role-based access control, guardrails,
a tool-calling agent, streaming chat UI, observability, and an automated evaluation harness.
**All seed documents are fictional** and are not real bank policies or regulations.

```
Angular 19 UI  ->  ASP.NET Core minimal API  ->  RagPipeline  ->  Hybrid retrieval (BM25 + vectors, RRF) -> rerank -> LLM
   chat (SSE)        JWT auth, rate limit        guardrails          ACL filter before scoring              (Mock | Ollama | Gemini | Azure OpenAI)
   agent, docs       correlation ids, CORS       confidence gate     per-stage timings, citations
```

## Live demo (GitHub Pages)
The Pages site runs the **same pipeline compiled to TypeScript in your browser** (`web/src/app/engine`), because GitHub Pages cannot run the .NET API. It is checked against the same golden set (`npm run eval:demo` in `web/`). The real .NET backend lives in `src/`; deploy it with the `Dockerfile` to any container host (Azure Container Apps, Render, Fly.io) and point the UI at it by setting `window.API_URL` in `web/public/config.js`.

## Quick start (Windows / macOS / Linux, .NET 8 SDK + Node 20+)
```bash
dotnet run --project src/PolicyPilot.Eval -- all        # 60 unit tests + RAG evaluation (offline, no API key)
dotnet run --project src/PolicyPilot.Api --urls http://localhost:5080
cd web && npm install && npm start                       # http://localhost:4200
```
Demo logins: `customer/customer123`, `officer/officer123`, `admin/admin123` (override with env vars / config; change `Jwt:Secret` outside dev).
Not logged in = customer access. Officer-only documents are never retrieved for customers.

## Choosing the LLM (the pipeline code does not change)
| Provider | Config |
|---|---|
| mock (default) | extractive answerer, deterministic, used in CI |
| Ollama (free, local) | `Llm__Provider=ollama Llm__BaseUrl=http://localhost:11434 Llm__ChatModel=llama3.2 Llm__EmbeddingModel=nomic-embed-text` (run `ollama pull` for both) |
| Gemini (free tier) | `Llm__Provider=gemini Llm__ApiKey=<key> Llm__ChatModel=gemini-2.0-flash Llm__EmbeddingModel=text-embedding-004` |
| Azure OpenAI | `Llm__Provider=azure Llm__BaseUrl=https://<resource>.openai.azure.com Llm__ApiKey=<key> Llm__ChatModel=<chat deployment> Llm__EmbeddingModel=<embedding deployment>` |

Model names change over time - check the provider's current list. Evaluate again after switching: `LLM_PROVIDER=ollama LLM_BASE_URL=... dotnet run --project src/PolicyPilot.Eval -- eval`.

## What is implemented
- Heading-aware chunking with overlap; BM25 + vector search fused by Reciprocal Rank Fusion; heuristic reranker.
- ACL applied inside retrieval (before scoring), so restricted text never reaches the prompt.
- Guardrails: prompt-injection detection on input and on retrieved text (sentence-level sanitising), PII redaction (Luhn-checked cards, PAN, email, phone), citation validation and groundedness check on output, confidence gate with "restricted topic" and unknown-term checks.
- Agent: JSON tool-call loop (policy_search, emi_calculator, loan_eligibility), max steps, repeat detection, tool allow-list.
- Security: PBKDF2 password hashing, HS256 JWT (alg=none rejected), rate limiting, upload limits and extension allow-list, admin-only document management.
- Observability: ActivitySource + Meter (OpenTelemetry compatible), per-stage timings returned with every answer, token counters, correlation ids.
- Evaluation: golden set (hit@k, MRR, key-fact match, citation accuracy, refusals, ACL no-leak) that fails CI below targets.

## Honest limitations
Embeddings in mock mode are hashing-based (lexical), so semantic recall comes from the real embedding providers. The vector index is brute force (fine for thousands of chunks; use Azure AI Search / pgvector beyond that).
Heuristic injection detection is a first line of defence only. The golden set is small and written by the author; grow it from real queries.
See `docs/ARCHITECTURE.md`.

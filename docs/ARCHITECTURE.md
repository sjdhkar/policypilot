# Architecture and design decisions

## Request flow
1. **Input guardrails** - block prompt injection, redact PII (so it is never sent to the LLM or logged).
2. **Contextualise** - short follow-ups ("what about it?") are expanded with the previous user turn.
3. **Hybrid retrieval** - BM25 (exact terms, numbers, acronyms) and cosine search (meaning) each return top-20, **only over chunks the user's role may read**; Reciprocal Rank Fusion (k=60) merges ranks without needing comparable scores; a reranker rescoring on coverage, phrase and section-title match picks the final 4.
4. **Indirect-injection filter** - offending sentences inside retrieved chunks are removed; the rest of the chunk is kept.
5. **Confidence gate** - refuse when the best chunk covers too few query terms, when the query names a term that only exists in documents the user cannot see, or when it is mostly unknown vocabulary. A refusal is cheaper than a wrong answer in a regulated domain.
6. **Generate** - strict system rules: answer only from context, cite [n], say "could not find" otherwise.
7. **Output guardrails** - strip invalid citations, redact PII, check that each sentence is supported by the context (groundedness); otherwise refuse.
8. **Response** - answer, citations, confidence, flags and per-stage timings.

## Key trade-offs
| Decision | Why | Alternative / when to change |
|---|---|---|
| Hybrid BM25 + vector | policy text has exact figures and acronyms that embeddings blur | add a cross-encoder reranker (Cohere/Azure semantic ranker) when recall@k is the bottleneck |
| ACL before scoring | prevents leakage and prompt contamination | push the filter into the vector DB as metadata filter at scale |
| Framework-only core | no dependency risk, every algorithm is visible | production: Azure AI Search, Microsoft.Extensions.AI, OpenTelemetry SDK |
| Mock LLM + eval gate | deterministic CI, regression safety | add LLM-as-judge scoring with a stronger model for open-ended answers |
| Refuse when unsure | wrong policy answers carry compliance risk | tune thresholds from the eval set, not by feel |
| Brute-force vector index | exact, simple, fast below ~100k chunks | HNSW (pgvector / AI Search) beyond that |

## Scaling to Azure
API on Azure Container Apps or App Service; Azure OpenAI for chat + embeddings; Azure AI Search (hybrid + semantic ranker + security filters); Blob Storage + queue-triggered ingestion; Key Vault for secrets; Entra ID instead of the demo JWT; Application Insights via the OpenTelemetry exporter; Redis for distributed rate limiting and semantic cache.

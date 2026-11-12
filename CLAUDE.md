# PolicyPilot - notes for Claude Code
- .NET 8, framework-only core (no NuGet packages on purpose). Run `dotnet run --project src/PolicyPilot.Eval -- all` after every change: it runs 60 unit tests and the golden-set RAG evaluation (gates on quality targets).
- Core pipeline: src/PolicyPilot.Core/Rag/RagPipeline.cs. Guardrails in Core/Guardrails, agent in Core/Agent, auth in Core/Security.
- API: src/PolicyPilot.Api/Program.cs (minimal API). UI: web/ (Angular 19, standalone, signals).
- Seed documents in data/docs are FICTIONAL. Golden eval set: data/eval/golden.json - add a case whenever you fix a retrieval/answer bug.
- Real LLM: set Llm:Provider to ollama | gemini | azure (see README). `tools/fake_llm_server.py` + `Eval -- providers <url>` verifies the HTTP clients offline.

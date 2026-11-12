using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PolicyPilot.Core.Observability;

/// <summary>
/// OpenTelemetry-compatible instrumentation using only System.Diagnostics (ActivitySource + Meter).
/// Any OpenTelemetry SDK / Application Insights exporter can subscribe to these without code changes.
/// </summary>
public sealed class Telemetry : IDisposable
{
    public const string Name = "PolicyPilot";
    public ActivitySource Source { get; } = new(Name);
    private readonly Meter _meter = new(Name);
    public Counter<long> Answered { get; }
    public Counter<long> NoAnswer { get; }
    public Counter<long> GuardrailTrips { get; }
    public Counter<long> PromptTokens { get; }
    public Counter<long> CompletionTokens { get; }
    public Histogram<double> LatencyMs { get; }

    private long _answered, _noAnswer, _guardrails, _promptTokens, _completionTokens, _requests;
    private readonly object _gate = new();
    private readonly List<double> _latencies = new();

    public Telemetry()
    {
        Answered = _meter.CreateCounter<long>("policypilot.answers");
        NoAnswer = _meter.CreateCounter<long>("policypilot.no_answer");
        GuardrailTrips = _meter.CreateCounter<long>("policypilot.guardrail_trips");
        PromptTokens = _meter.CreateCounter<long>("policypilot.tokens.prompt");
        CompletionTokens = _meter.CreateCounter<long>("policypilot.tokens.completion");
        LatencyMs = _meter.CreateHistogram<double>("policypilot.request.latency", "ms");
    }

    public void RecordLlm(string model, int prompt, int completion)
    {
        PromptTokens.Add(prompt, new KeyValuePair<string, object?>("model", model));
        CompletionTokens.Add(completion, new KeyValuePair<string, object?>("model", model));
        lock (_gate) { _promptTokens += prompt; _completionTokens += completion; }
    }

    public void RecordRequest(double ms, bool answered)
    {
        LatencyMs.Record(ms);
        lock (_gate)
        {
            _requests++; if (answered) _answered++; else _noAnswer++;
            _latencies.Add(ms); if (_latencies.Count > 1000) _latencies.RemoveAt(0);
        }
    }

    public void RecordGuardrail() { lock (_gate) _guardrails++; }

    /// <summary>Small JSON-friendly snapshot for the demo dashboard (real deployments read the exported OTel metrics instead).</summary>
    public object Snapshot()
    {
        lock (_gate)
        {
            var sorted = _latencies.OrderBy(x => x).ToList();
            double P(double p) => sorted.Count == 0 ? 0 : Math.Round(sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))], 1);
            return new { requests = _requests, answered = _answered, noAnswer = _noAnswer, guardrailTrips = _guardrails,
                         promptTokens = _promptTokens, completionTokens = _completionTokens, p50Ms = P(0.5), p95Ms = P(0.95) };
        }
    }

    public void Dispose() { Source.Dispose(); _meter.Dispose(); }
}

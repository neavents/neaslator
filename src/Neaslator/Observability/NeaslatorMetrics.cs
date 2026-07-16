using System.Diagnostics.Metrics;

namespace Neaslator.Observability;

public static class NeaslatorMetrics
{
    private static readonly Meter Meter = new("Neaslator", "1.0.0");

    public static readonly Counter<long> CacheLookups =
        Meter.CreateCounter<long>("neaslator.cache.lookups", "{lookups}", "Total cache lookup operations by level and result");
    public static readonly Counter<long> CacheCollisions =
        Meter.CreateCounter<long>("neaslator.cache.hash_collision_total", "{collisions}", "Hash collisions detected during cache lookups");
    public static readonly Counter<long> CacheBackfills =
        Meter.CreateCounter<long>("neaslator.cache.backfill_total", "{backfills}", "L2-to-L1 cache backfill operations");

    public static readonly Counter<long> ProviderRequests =
        Meter.CreateCounter<long>("neaslator.provider.requests_total", "{requests}", "Translation provider requests by provider and status");
    public static readonly Counter<long> ProviderTokensUsed =
        Meter.CreateCounter<long>("neaslator.provider.tokens_used", "{tokens}", "LLM tokens consumed by provider and type");
    public static readonly Counter<double> ProviderCostCents =
        Meter.CreateCounter<double>("neaslator.provider.cost_cents", "cents", "Estimated provider cost in cents");
    public static readonly Histogram<double> ProviderLatencySeconds =
        Meter.CreateHistogram<double>("neaslator.provider.latency_seconds", "s", "Provider request latency in seconds");
    public static readonly Counter<long> ProviderFallbacks =
        Meter.CreateCounter<long>("neaslator.provider.fallbacks_total", "{fallbacks}", "Provider fallback events");
    public static readonly Counter<double> ProviderCostEstimateCents =
        Meter.CreateCounter<double>("neaslator.provider.cost_estimate_cents", "cents", "Cost estimate based on token counts and per-provider pricing");

    public static readonly Histogram<double> SagaDurationSeconds =
        Meter.CreateHistogram<double>("neaslator.saga.duration_seconds", "s", "End-to-end saga duration in seconds");
    public static readonly Counter<long> SagaSuperseded =
        Meter.CreateCounter<long>("neaslator.saga.superseded_total", "{events}", "Sagas superseded by newer events");

    public static readonly Counter<long> ItemsProcessed =
        Meter.CreateCounter<long>("neaslator.pipeline.items_processed", "{items}", "Translation units processed by source");
    public static readonly Histogram<double> PipelineDurationSeconds =
        Meter.CreateHistogram<double>("neaslator.pipeline.duration_seconds", "s", "End-to-end translation pipeline duration in seconds");
    public static readonly Histogram<int> PipelineLanguagesPerRun =
        Meter.CreateHistogram<int>("neaslator.pipeline.languages_per_run", "{languages}", "Number of target languages processed per pipeline run");
    public static readonly Counter<long> PipelineDiffChangedUnits =
        Meter.CreateCounter<long>("neaslator.pipeline.diff_changed_units", "{units}", "Translation units identified as changed by the diff engine");

    public static readonly Counter<long> DebounceCoalescedTotal =
        Meter.CreateCounter<long>("neaslator.debounce.coalesced_total", "{events}", "Menu publish events coalesced by debounce");
    public static readonly Counter<long> DebounceTriggeredTotal =
        Meter.CreateCounter<long>("neaslator.debounce.triggered_total", "{events}", "Debounce windows that triggered a translation");

    public static readonly Counter<long> LockAcquiredTotal =
        Meter.CreateCounter<long>("neaslator.lock.acquired_total", "{locks}", "Distributed locks successfully acquired on first attempt");
    public static readonly Counter<long> LockWaitedTotal =
        Meter.CreateCounter<long>("neaslator.lock.waited_total", "{waits}", "Distributed lock acquisitions resolved by peer completing translation");
    public static readonly Counter<long> LockForcedTotal =
        Meter.CreateCounter<long>("neaslator.lock.forced_total", "{forces}", "Distributed lock forced acquisitions after wait timeout");

    public static readonly Counter<long> QualityUpgradeEntriesUpgraded =
        Meter.CreateCounter<long>("neaslator.quality_upgrade.entries_upgraded_total", "{entries}", "Translation memory entries upgraded from degraded provider");
    public static readonly Counter<long> QualityUpgradeEntriesScanned =
        Meter.CreateCounter<long>("neaslator.quality_upgrade.entries_scanned_total", "{entries}", "Translation memory entries scanned during quality upgrade");
    public static readonly Counter<long> QualityUpgradeFailures =
        Meter.CreateCounter<long>("neaslator.quality_upgrade.failures_total", "{failures}", "Quality upgrade batch failures");

    public static readonly Counter<long> OnDemandRequestsTotal =
        Meter.CreateCounter<long>("neaslator.on_demand.requests_total", "{requests}", "On-demand translation requests by source");
    public static readonly Histogram<double> OnDemandLatencySeconds =
        Meter.CreateHistogram<double>("neaslator.on_demand.latency_seconds", "s", "On-demand translation latency in seconds");

    public static readonly Counter<long> NotificationsSent =
        Meter.CreateCounter<long>("neaslator.notifications.sent_total", "{notifications}", "SignalR notifications sent by type");
}

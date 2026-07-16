using System.Diagnostics;

namespace Neaslator.Observability;

public static class NeaslatorActivitySources
{
    public const string PipelineName = "Neaslator.Pipeline";
    public const string CacheName = "Neaslator.Cache";
    public const string ProviderName = "Neaslator.Provider";
    public const string SagaName = "Neaslator.Saga";
    public const string LockName = "Neaslator.Lock";
    public const string DebounceName = "Neaslator.Debounce";
    public const string QualityUpgradeName = "Neaslator.QualityUpgrade";
    public const string OnDemandName = "Neaslator.OnDemand";

    public static readonly ActivitySource Pipeline = new(PipelineName);
    public static readonly ActivitySource Cache = new(CacheName);
    public static readonly ActivitySource Provider = new(ProviderName);
    public static readonly ActivitySource Saga = new(SagaName);
    public static readonly ActivitySource Lock = new(LockName);
    public static readonly ActivitySource Debounce = new(DebounceName);
    public static readonly ActivitySource QualityUpgrade = new(QualityUpgradeName);
    public static readonly ActivitySource OnDemand = new(OnDemandName);

    public static readonly string[] AllSourceNames =
    [
        PipelineName,
        CacheName,
        ProviderName,
        SagaName,
        LockName,
        DebounceName,
        QualityUpgradeName,
        OnDemandName
    ];
}

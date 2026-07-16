namespace Neaslator.Infrastructure.Cache;

public sealed record CacheLookupResult(
    string TargetLanguageCode,
    CachedTranslation? Translation,
    CacheSource Source);

public enum CacheSource
{
    L1Garnet,
    L2PostgreSql,
    Miss
}

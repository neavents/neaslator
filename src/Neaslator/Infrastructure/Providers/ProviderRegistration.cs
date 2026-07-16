using Polly;

namespace Neaslator.Infrastructure.Providers;

public sealed class ProviderRegistration
{
    public required ITranslationProvider Provider { get; init; }
    public required ResiliencePipeline Pipeline { get; init; }
    public bool IsAvailable { get; set; } = true;
}

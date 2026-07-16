namespace Neaslator.Infrastructure.Providers;

public interface ITranslationRouter
{
    Task<TranslationBatchResult> TranslateAsync(
        TranslationBatchRequest request,
        CancellationToken cancellationToken);
}

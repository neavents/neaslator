namespace Neaslator.Domain.Enums;

public enum TranslationSagaState
{
    Debouncing,
    ComputingDiff,
    ResolvingCache,
    Translating,
    Completed,
    PartiallyCompleted,
    Failed,
    Superseded
}

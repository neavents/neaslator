using MassTransit;
using Neavents.Messaging.Contracts.Settings;

namespace Neaslator.Infrastructure.LanguagePolicy;

/// <summary>Drops an organisation's cached translation languages when identity says they, or how venues relate to them, changed.</summary>
public sealed class TranslationLanguageSettingsChangedConsumer : IConsumer<SettingsChanged>
{
    public const string SettingKey = "menu.translation.languages";
    public const string ManageTogetherKey = "org.manage_together";

    private readonly ITranslationLanguagePolicySource _policies;

    public TranslationLanguageSettingsChangedConsumer(ITranslationLanguagePolicySource policies) => _policies = policies;

    public Task Consume(ConsumeContext<SettingsChanged> context)
    {
        SettingsChanged message = context.Message;
        return message.OwningService == "identity" && (message.Keys.Contains(SettingKey) || message.Keys.Contains(ManageTogetherKey))
            ? _policies.ForgetOrganisationAsync(message.OrgId, context.CancellationToken)
            : Task.CompletedTask;
    }
}

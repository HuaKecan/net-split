using NetSplit.Core;

namespace NetSplit.Service;

public interface IConfigurationValidatorFacade
{
    ConfigurationValidationResult Validate(
        SplitRouteSettings settings,
        IReadOnlyList<NetworkAdapterSnapshot> adapters);
}

public sealed class ConfigurationValidatorFacade : IConfigurationValidatorFacade
{
    public ConfigurationValidationResult Validate(
        SplitRouteSettings settings,
        IReadOnlyList<NetworkAdapterSnapshot> adapters)
    {
        return ConfigurationValidator.Validate(settings, adapters);
    }
}

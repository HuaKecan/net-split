namespace NetSplit.Service;

internal sealed class ResidentialProxyHealthTracker
{
    private const int DefaultFailureThreshold = 2;
    private const int DefaultRecoveryThreshold = 2;

    private readonly int _failureThreshold;
    private readonly int _recoveryThreshold;
    private bool? _stableHealth;
    private bool? _candidateHealth;
    private int _candidateCount;

    public ResidentialProxyHealthTracker(
        int failureThreshold = DefaultFailureThreshold,
        int recoveryThreshold = DefaultRecoveryThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failureThreshold, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(recoveryThreshold, 1);

        _failureThreshold = failureThreshold;
        _recoveryThreshold = recoveryThreshold;
    }

    public bool? Observe(bool? snapshotHealth, bool delayProbeSucceeded)
    {
        if (delayProbeSucceeded)
        {
            _stableHealth = true;
            ResetCandidate();
            return true;
        }

        var observedHealth = snapshotHealth;
        if (!observedHealth.HasValue)
        {
            return _stableHealth;
        }

        if (_stableHealth is null && observedHealth.Value)
        {
            _stableHealth = true;
            ResetCandidate();
            return _stableHealth;
        }

        if (_stableHealth == observedHealth)
        {
            ResetCandidate();
            return _stableHealth;
        }

        if (_candidateHealth == observedHealth)
        {
            _candidateCount++;
        }
        else
        {
            _candidateHealth = observedHealth;
            _candidateCount = 1;
        }

        var threshold = observedHealth.Value
            ? _recoveryThreshold
            : _failureThreshold;
        if (_candidateCount >= threshold)
        {
            _stableHealth = observedHealth;
            ResetCandidate();
        }

        return _stableHealth;
    }

    public void Reset()
    {
        _stableHealth = null;
        ResetCandidate();
    }

    private void ResetCandidate()
    {
        _candidateHealth = null;
        _candidateCount = 0;
    }
}

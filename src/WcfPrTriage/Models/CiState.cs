namespace WcfPrTriage.Models;

/// <summary>High-level CI state used both for a whole PR and for an individual build/check.</summary>
public enum CiState
{
    Unknown,
    Pending,
    Running,
    Success,
    Failure,
}

/// <summary>Rolls a set of build/check states up into one PR-level state.</summary>
public static class CiStateAggregation
{
    /// <summary>Overall PR state from its CI builds: failure wins, then in-progress, then success.</summary>
    public static CiState Overall(IEnumerable<CheckBuild> builds)
    {
        bool anyRunning = false, anySuccess = false;
        foreach (var b in builds)
        {
            if (b.State == CiState.Failure)
                return CiState.Failure;   // failure dominates — short-circuit
            if (b.State is CiState.Running or CiState.Pending)
                anyRunning = true;
            else if (b.State == CiState.Success)
                anySuccess = true;
        }
        return anyRunning ? CiState.Running
             : anySuccess ? CiState.Success
             : CiState.Unknown;
    }
}

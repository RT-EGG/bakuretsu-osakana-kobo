namespace BakuretsuOsakanaKobo;

internal sealed class TemporaryPlaybackRateGesture
{
    internal static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(400);

    private double _startX;
    private double _startY;
    private float _rateBeforeActivation;

    public bool IsPending { get; private set; }

    public bool IsActive { get; private set; }

    public void Begin(double x, double y)
    {
        _startX = x;
        _startY = y;
        IsPending = true;
        IsActive = false;
    }

    public bool CancelIfMoved(
        double x,
        double y,
        double horizontalThreshold,
        double verticalThreshold)
    {
        if (!IsPending || IsActive)
        {
            return false;
        }

        if (Math.Abs(x - _startX) <= horizontalThreshold &&
            Math.Abs(y - _startY) <= verticalThreshold)
        {
            return false;
        }

        Cancel();
        return true;
    }

    public bool TryActivate(bool canActivate, float currentRate)
    {
        if (!IsPending || !canActivate)
        {
            Cancel();
            return false;
        }

        _rateBeforeActivation = currentRate;
        IsPending = false;
        IsActive = true;
        return true;
    }

    public float? End()
    {
        float? rateToRestore = IsActive ? _rateBeforeActivation : null;
        Cancel();
        return rateToRestore;
    }

    public void Cancel()
    {
        IsPending = false;
        IsActive = false;
    }
}

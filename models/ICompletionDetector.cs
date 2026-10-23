namespace RoboCopyUI
{
    public interface ICompletionDetector
    {
        long CheckAndReap(InFlightSet inFlight, out int filesCompleted);
        long GetInFlightPartialBytes(InFlightSet inFlight);
    }
}
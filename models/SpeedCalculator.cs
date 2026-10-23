namespace RoboCopyUI
{
    public class SpeedCalculator
    {
        private double _smoothed;
        // Robocopy's process-I/O counter is updated in blocks. A conservative
        // average prevents one block from making the displayed rate jump.
        private const double Alpha = 0.3; // Increased for better responsiveness

        public double Current => _smoothed;

        public void Reset() => _smoothed = 0;

        public double Update(double rawSampleBytesPerSecond)
        {
            _smoothed = _smoothed == 0
                ? rawSampleBytesPerSecond
                : Alpha * rawSampleBytesPerSecond + (1 - Alpha) * _smoothed;
            return _smoothed;
        }
    }
}

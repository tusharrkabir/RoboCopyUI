using System;

namespace RoboCopyUI
{
    public class EtaEstimator
    {
        private TimeSpan? _lastDisplayed;
        private DateTime _lastUpdateUtc = DateTime.MinValue;
        private TimeSpan _smoothedEta;
        private bool _isSmoothedEtaInitialized;

        private static readonly TimeSpan MinUpdateInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan ChangeThreshold = TimeSpan.FromSeconds(2);
        private const double EtaAlpha = 0.25;

        // Speed near-zero during warmup was producing an astronomically large ETA.
        // Below this floor, treat speed as "not reliable yet" and show null ("--:--").
        private const double MinReliableSpeedBytesPerSecond = 50 * 1024; // 50 KB/s

        public TimeSpan? Update(long remainingBytes, double smoothedAverageSpeedBytesPerSecond)
        {
            if (smoothedAverageSpeedBytesPerSecond < MinReliableSpeedBytesPerSecond)
                return null; // not enough signal yet — display falls back to "--:--"

            var rawSeconds = remainingBytes / smoothedAverageSpeedBytesPerSecond;
            var rawEta = TimeSpan.FromSeconds(rawSeconds);

            if (!_isSmoothedEtaInitialized)
            {
                _smoothedEta = rawEta;
                _isSmoothedEtaInitialized = true;
            }
            else
            {
                var smoothedSeconds = EtaAlpha * rawSeconds + (1 - EtaAlpha) * _smoothedEta.TotalSeconds;
                _smoothedEta = TimeSpan.FromSeconds(smoothedSeconds);
            }

            var now = DateTime.UtcNow;
            var dueForUpdate = now - _lastUpdateUtc >= MinUpdateInterval;
            var changedEnough = _lastDisplayed == null ||
                                 (_smoothedEta - _lastDisplayed.Value).Duration() >= ChangeThreshold;

            if (dueForUpdate && changedEnough)
            {
                _lastDisplayed = _smoothedEta;
                _lastUpdateUtc = now;
            }

            return _lastDisplayed ?? _smoothedEta;
        }
    }
}
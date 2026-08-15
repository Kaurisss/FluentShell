using System.Globalization;

namespace FluentShell.Services;

public sealed class LinuxCpuUsageCalculator
{
    private CpuTimes? _previousSample;

    public double? AddSample(string procStatLine)
    {
        if (!CpuTimes.TryParse(procStatLine, out var currentSample) || currentSample is null)
            return null;

        var previousSample = _previousSample;
        _previousSample = currentSample;
        if (previousSample is null)
            return null;

        var totalDelta = currentSample.Total - previousSample.Total;
        var idleDelta = currentSample.Idle - previousSample.Idle;
        if (totalDelta <= 0 || idleDelta < 0)
            return null;

        return Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0, 100);
    }

    private sealed record CpuTimes(long Total, long Idle)
    {
        public static bool TryParse(string procStatLine, out CpuTimes? cpuTimes)
        {
            cpuTimes = null;
            var values = procStatLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (values.Length < 5 || !string.Equals(values[0], "cpu", StringComparison.Ordinal))
                return false;

            var counters = new long[8];
            for (var index = 0; index < counters.Length; index++)
            {
                if (index + 1 >= values.Length ||
                    !long.TryParse(values[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out counters[index]) ||
                    counters[index] < 0)
                    return false;
            }

            var total = counters.Sum();
            var idle = counters[3] + counters[4];
            cpuTimes = new CpuTimes(total, idle);
            return true;
        }
    }
}
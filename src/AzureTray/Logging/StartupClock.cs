using System;
using System.Diagnostics;

namespace AzureTray.Logging;

// Measures elapsed time from process start so the startup narrative can say
// how long coming up actually took. Constructed as the first thing Main does
// and registered as a singleton, so every later stage reads the same origin.
// Stopwatch rather than DateTime arithmetic: it is monotonic, so a clock
// change or DST shift mid-startup cannot produce a negative or absurd figure.
internal sealed class StartupClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public TimeSpan Elapsed => _stopwatch.Elapsed;
}

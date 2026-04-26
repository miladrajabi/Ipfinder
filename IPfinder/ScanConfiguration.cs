using System.Net;

namespace IPfinder;

public readonly record struct ScanConfiguration(
    IReadOnlyList<IPAddress> Addresses,
    int Port,
    int TimeoutMs,
    int Concurrency);

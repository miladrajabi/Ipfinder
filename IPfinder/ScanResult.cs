namespace IPfinder;

public sealed class ScanResult(string ip, bool pingSucceeded, bool tcpTestSucceeded, long ms)
{
    public string IpAddress { get; } = ip;
    public bool PingSucceeded { get; } = pingSucceeded;
    public bool TcpTestSucceeded { get; } = tcpTestSucceeded;
    public long RoundTripMs { get; } = ms;
    public string PingSucceededText => PingSucceeded ? "True" : "False";
    public string TcpTestSucceededText => TcpTestSucceeded ? "True" : "False";
    public string RoundTripText => RoundTripMs > 0 ? $"{RoundTripMs}ms" : "Timeout/Error";
    public string Status => TcpTestSucceeded ? "Open" : "Closed";
}

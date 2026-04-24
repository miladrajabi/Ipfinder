namespace IPfinder;

public class ScanResult(string ip, string status, long ms)
{
    public string IpAddress { get; set; } = ip;
    public string Status { get; set; } = status;
    public long RoundTripMs { get; set; } = ms;
    public string RoundTripText => RoundTripMs > 0 ? $"{RoundTripMs}ms" : "Timeout/Error";
}
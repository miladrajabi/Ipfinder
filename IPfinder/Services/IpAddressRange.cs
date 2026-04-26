using System.Net;

namespace IPfinder.Services;

public static class IpAddressRange
{
    public static bool TryCreate(
        IPAddress startIp,
        IPAddress endIp,
        int maxRange,
        out IReadOnlyList<IPAddress> addresses,
        out string errorTitle,
        out string errorMessage)
    {
        addresses = Array.Empty<IPAddress>();
        errorTitle = "Error";
        errorMessage = string.Empty;

        if (startIp.GetAddressBytes().Length != 4 || endIp.GetAddressBytes().Length != 4)
        {
            errorMessage = "Only IPv4 addresses are supported.";
            return false;
        }

        var start = ToUInt32(startIp);
        var end = ToUInt32(endIp);

        if (start > end)
        {
            errorMessage = "Start IP must be less than or equal to End IP.";
            return false;
        }

        var totalCount = end - start + 1;
        if (totalCount > maxRange)
        {
            errorTitle = "Limit";
            errorMessage = $"Max {maxRange} IPs per scan.";
            return false;
        }

        addresses = Enumerable.Range(0, (int)totalCount)
            .Select(index => FromUInt32(start + (uint)index))
            .ToList();

        return true;
    }

    private static uint ToUInt32(IPAddress ipAddress)
    {
        var bytes = ipAddress.GetAddressBytes();
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return BitConverter.ToUInt32(bytes, 0);
    }

    private static IPAddress FromUInt32(uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return new IPAddress(bytes);
    }
}

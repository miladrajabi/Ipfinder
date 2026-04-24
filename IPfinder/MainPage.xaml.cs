using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;

namespace IPfinder;

public partial class MainPage : ContentPage
{
    private const int DefaultPort = 443;
    private const int DefaultTimeoutMs = 1000;
    private const int DefaultConcurrency = 16;
    private const int MaxScanRange = 5000;

    private static readonly HttpClient IpInfoClient = new();

    public ObservableCollection<ScanResult> Results { get; } = new();

    private CancellationTokenSource? _scanCts;

    public MainPage()
    {
        InitializeComponent();
        BindingContext = this;
        ApplyDefaultValues();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await UpdateIpInfoAsync();
    }

    private async void OnStartClicked(object? sender, EventArgs e)
    {
        if (_scanCts is not null)
        {
            return;
        }

        if (!TryGetScanConfiguration(out var configuration, out var errorTitle, out var errorMessage))
        {
            await DisplayAlertAsync(errorTitle, errorMessage, "OK");
            return;
        }

        Results.Clear();
        _scanCts = new CancellationTokenSource();
        SetUiState(isScanning: true);
        StatusLabel.Text = $"Scanning {configuration.Addresses.Count} IPs...";

        try
        {
            await Parallel.ForEachAsync(
                configuration.Addresses,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = configuration.Concurrency,
                    CancellationToken = _scanCts.Token
                },
                async (ip, ct) =>
                {
                    var result = await ScanTcpAsync(ip, configuration.Port, configuration.TimeoutMs, ct);
                    if (result.Status == "Open")
                    {
                        MainThread.BeginInvokeOnMainThread(() => Results.Add(result));
                    }
                });

            StatusLabel.Text = "Finished.";
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Scan stopped.";
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;
            SetUiState(isScanning: false);
        }
    }

    private async Task<ScanResult> ScanTcpAsync(IPAddress ip, int port, int timeoutMs, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();

        try
        {
            var stopwatch = Stopwatch.StartNew();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);

            await client.ConnectAsync(ip, port, timeoutCts.Token);
            stopwatch.Stop();

            return new ScanResult(ip.ToString(), "Open", stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ScanResult(ip.ToString(), "Closed", 0);
        }
        catch (SocketException)
        {
            return new ScanResult(ip.ToString(), "Closed", 0);
        }
        catch (ObjectDisposedException)
        {
            return new ScanResult(ip.ToString(), "Closed", 0);
        }
    }

    private void OnStopClicked(object? sender, EventArgs e) => _scanCts?.Cancel();

    private void OnClearClicked(object? sender, EventArgs e)
    {
        if (_scanCts is not null)
        {
            return;
        }

        Results.Clear();
        StatusLabel.Text = "Ready.";
    }

    private bool TryGetScanConfiguration(
        out ScanConfiguration configuration,
        out string errorTitle,
        out string errorMessage)
    {
        configuration = default;
        errorTitle = "Error";
        errorMessage = string.Empty;

        if (!IPAddress.TryParse(StartIpEntry.Text?.Trim(), out var startIp) ||
            !IPAddress.TryParse(EndIpEntry.Text?.Trim(), out var endIp))
        {
            errorMessage = "IP addresses are invalid.";
            return false;
        }

        if (!TryParsePositiveInt(PortEntry.Text, DefaultPort, out var port))
        {
            errorMessage = "Port must be a positive number.";
            return false;
        }

        if (!TryParsePositiveInt(TimeoutEntry.Text, DefaultTimeoutMs, out var timeoutMs))
        {
            errorMessage = "Timeout must be a positive number.";
            return false;
        }

        if (!TryParsePositiveInt(ConcurrencyEntry.Text, DefaultConcurrency, out var concurrency))
        {
            errorMessage = "Concurrency must be a positive number.";
            return false;
        }

        var start = IpToUInt32(startIp);
        var end = IpToUInt32(endIp);

        if (start > end)
        {
            errorMessage = "Start IP must be less than or equal to End IP.";
            return false;
        }

        var totalCount = end - start + 1;
        if (totalCount > MaxScanRange)
        {
            errorTitle = "Limit";
            errorMessage = $"Max {MaxScanRange} IPs per scan.";
            return false;
        }

        var addresses = Enumerable.Range(0, (int)totalCount)
            .Select(index => UInt32ToIp(start + (uint)index))
            .ToList();

        configuration = new ScanConfiguration(addresses, port, timeoutMs, concurrency);
        return true;
    }

    private static bool TryParsePositiveInt(string? value, int fallback, out int result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = fallback;
            return true;
        }

        if (int.TryParse(value.Trim(), out result) && result > 0)
        {
            return true;
        }

        result = fallback;
        return false;
    }

    private void SetUiState(bool isScanning)
    {
        StartButton.IsEnabled = !isScanning;
        StopButton.IsEnabled = isScanning;
        ClearButton.IsEnabled = !isScanning;
        StartIpEntry.IsEnabled = !isScanning;
        EndIpEntry.IsEnabled = !isScanning;
        PortEntry.IsEnabled = !isScanning;
        TimeoutEntry.IsEnabled = !isScanning;
        ConcurrencyEntry.IsEnabled = !isScanning;
    }

    private void ApplyDefaultValues()
    {
        StartIpEntry.Text = "172.64.0.0";
        EndIpEntry.Text = "172.64.15.255";
        PortEntry.Text = DefaultPort.ToString();
        TimeoutEntry.Text = DefaultTimeoutMs.ToString();
        ConcurrencyEntry.Text = DefaultConcurrency.ToString();
    }

    private static uint IpToUInt32(IPAddress ipAddress)
    {
        var bytes = ipAddress.GetAddressBytes();
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return BitConverter.ToUInt32(bytes, 0);
    }

    private static IPAddress UInt32ToIp(uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return new IPAddress(bytes);
    }

    private async Task UpdateIpInfoAsync()
    {
        try
        {
            var response = await IpInfoClient.GetFromJsonAsync<IpLocationResponse>("http://ip-api.com/json/");
            if (response is null)
            {
                SetIpInfoUnavailable();
                return;
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                MyIpLabel.Text = $"🌐 IP: {response.query}";
                MyCountryLabel.Text = $"📍 Location: {response.country} ({response.city}) - {response.isp}";
            });
        }
        catch
        {
            SetIpInfoUnavailable();
        }
    }

    private void SetIpInfoUnavailable()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            MyIpLabel.Text = "خطا در اتصال";
            MyCountryLabel.Text = "احتمالاً فیلترشکن خاموش است یا سرویس در دسترس نیست.";
        });
    }

    private readonly record struct ScanConfiguration(
        List<IPAddress> Addresses,
        int Port,
        int TimeoutMs,
        int Concurrency);
}

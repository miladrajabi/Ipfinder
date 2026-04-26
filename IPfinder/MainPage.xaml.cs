using IPfinder.Services;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net;

namespace IPfinder;

public partial class MainPage : ContentPage
{
    private const int DefaultPort = 443;
    private const int DefaultTimeoutMs = 1000;
    private const int DefaultConcurrency = 16;
    private const int MaxScanRange = 5000;
    private const int CompactLayoutWidth = 980;
    private const int ProgressUiUpdateInterval = 8;
    private const int ResultFlushInterval = 12;

    private readonly IpInfoService _ipInfoService = new();
    private readonly NetworkProbeService _networkProbeService = new();

    private bool _isUpdatingIpInfo;
    private bool _isCompactLayout;
    private CancellationTokenSource? _scanCts;

    public ObservableCollection<ScanResult> Results { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        BindingContext = this;
        ApplyDefaultValues();
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);

        if (width <= 0)
        {
            return;
        }

        ApplyResponsiveLayout(width < CompactLayoutWidth);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await SafeUpdateIpInfoAsync();
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

        var scanCts = new CancellationTokenSource();
        _scanCts = scanCts;

        var totalCount = configuration.Addresses.Count;
        var startedCount = 0;
        var completedCount = 0;
        var resultFlushInProgress = 0;
        var pendingResults = new ConcurrentQueue<ScanResult>();

        SetUiState(isScanning: true);
        await UpdateScanProgressAsync(0, totalCount, null);

        async Task FlushResultsAsync()
        {
            if (Interlocked.Exchange(ref resultFlushInProgress, 1) == 1)
            {
                return;
            }

            try
            {
                await RunOnMainThreadAsync(() =>
                {
                    while (pendingResults.TryDequeue(out var pendingResult))
                    {
                        Results.Add(pendingResult);
                    }
                });
            }
            finally
            {
                Volatile.Write(ref resultFlushInProgress, 0);
            }
        }

        try
        {
            await Parallel.ForEachAsync(
                configuration.Addresses,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = configuration.Concurrency,
                    CancellationToken = scanCts.Token
                },
                async (ip, ct) =>
                {
                    var started = Interlocked.Increment(ref startedCount);
                    if (ShouldUpdateProgress(started, totalCount))
                    {
                        await UpdateScanProgressAsync(started, totalCount, ip);
                    }

                    var result = await _networkProbeService.ProbeAsync(
                        ip,
                        configuration.Port,
                        configuration.TimeoutMs,
                        ct);

                    var completed = Interlocked.Increment(ref completedCount);
                    pendingResults.Enqueue(result);

                    if (completed % ResultFlushInterval == 0 || completed == totalCount)
                    {
                        await FlushResultsAsync();
                    }
                });

            await FlushResultsAsync();
            SetScanFinished(completedCount, totalCount);
        }
        catch (OperationCanceledException)
        {
            SetScanStopped(completedCount, totalCount);
        }
        catch (Exception ex)
        {
            await RunOnMainThreadAsync(() =>
            {
                StatusLabel.Text = $"Scan failed: {ex.Message}";
                CurrentScanIpLabel.Text = "Scan failed.";
                CurrentScanIpLabel.IsVisible = true;
            });
        }
        finally
        {
            await FlushResultsAsync();
            scanCts.Dispose();
            _scanCts = null;
            SetUiState(isScanning: false);
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
        ResetScanProgress();
    }

    private async void OnRefreshIpClicked(object? sender, EventArgs e)
    {
        await SafeUpdateIpInfoAsync();
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

        concurrency = NormalizeConcurrency(concurrency);

        if (!IpAddressRange.TryCreate(
                startIp,
                endIp,
                MaxScanRange,
                out var addresses,
                out errorTitle,
                out errorMessage))
        {
            return false;
        }

        configuration = new ScanConfiguration(addresses, port, timeoutMs, concurrency);
        return true;
    }

    private static int NormalizeConcurrency(int requestedConcurrency)
    {
        var maxConcurrency = DeviceInfo.Current.Platform == DevicePlatform.Android ||
                             DeviceInfo.Current.Platform == DevicePlatform.iOS
            ? 6
            : 32;

        return Math.Clamp(requestedConcurrency, 1, maxConcurrency);
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
        SetRefreshButtonState();
    }

    private Task UpdateScanProgressAsync(int current, int total, IPAddress? currentIp)
    {
        return RunOnMainThreadAsync(() =>
        {
            StatusLabel.Text = $"Scanning {current:N0}/{total:N0}";
            CurrentScanIpLabel.Text = currentIp is null
                ? "Current IP: --"
                : $"Current IP: {currentIp}";
            CurrentScanIpLabel.IsVisible = true;
            ScanProgressBar.IsVisible = true;
            ScanProgressBar.Progress = total == 0 ? 0 : Math.Clamp((double)current / total, 0, 1);
        });
    }

    private void SetScanFinished(int completed, int total)
    {
        StatusLabel.Text = $"Finished. {completed:N0}/{total:N0} scanned.";
        CurrentScanIpLabel.Text = "Scan complete.";
        CurrentScanIpLabel.IsVisible = true;
        ScanProgressBar.IsVisible = true;
        ScanProgressBar.Progress = 1;
    }

    private void SetScanStopped(int completed, int total)
    {
        StatusLabel.Text = $"Stopped at {completed:N0}/{total:N0}.";
        CurrentScanIpLabel.Text = "Scan stopped.";
        CurrentScanIpLabel.IsVisible = true;
    }

    private void ResetScanProgress()
    {
        CurrentScanIpLabel.Text = "Current IP: --";
        CurrentScanIpLabel.IsVisible = false;
        ScanProgressBar.Progress = 0;
        ScanProgressBar.IsVisible = false;
    }

    private static Task RunOnMainThreadAsync(Action action)
    {
        if (MainThread.IsMainThread)
        {
            action();
            return Task.CompletedTask;
        }

        return MainThread.InvokeOnMainThreadAsync(action);
    }

    private void SetRefreshButtonState()
    {
        var isEnabled = _scanCts is null && !_isUpdatingIpInfo;
        RefreshIpButton.IsEnabled = isEnabled;
        RefreshIpButton.Opacity = isEnabled ? 1 : 0.55;
    }

    private static bool ShouldUpdateProgress(int current, int total)
    {
        return current == 0 ||
               current == 1 ||
               current == total ||
               current % ProgressUiUpdateInterval == 0;
    }

    private void ApplyResponsiveLayout(bool useCompactLayout)
    {
        if (_isCompactLayout == useCompactLayout)
        {
            return;
        }

        _isCompactLayout = useCompactLayout;

        if (useCompactLayout)
        {
            RootGrid.Padding = new Thickness(12, 8, 12, 10);
            RootGrid.RowSpacing = 8;
            PageTitleLabel.FontSize = 22;
            PageSubtitleLabel.IsVisible = false;
            HeaderGrid.ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star)
            };
            HeaderBadge.IsVisible = false;

            IpInfoCard.Padding = new Thickness(12);
            MyIpLabel.FontSize = 16;
            MyCountryLabel.FontSize = 12;
            MyCountryLabel.MaxLines = 2;
            IpInfoCard.HeightRequest = -1;
            IpInfoCard.MinimumHeightRequest = 118;

            ScanSetupCard.Padding = new Thickness(10);
            ScanSetupGrid.RowSpacing = 10;
            ScanSubtitleLabel.IsVisible = false;
            ScanHeaderGrid.ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star)
            };
            ScanModeBadge.IsVisible = false;

            AddressInputsGrid.RowDefinitions = CreateAutoRows(3);
            AddressInputsGrid.ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star)
            };
            MoveToGrid(StartIpCard, 0, 0);
            MoveToGrid(EndIpCard, 0, 1);
            MoveToGrid(PortCard, 0, 2);
            SetInputCardPadding(10, 6);
            SetEntryHeight(32);

            OptionsInputsGrid.RowDefinitions = CreateAutoRows(2);
            OptionsInputsGrid.ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star)
            };
            MoveToGrid(TimeoutCard, 0, 0);
            MoveToGrid(ConcurrencyCard, 0, 1);

            ScanControlsGrid.RowDefinitions = CreateAutoRows(2);
            ScanControlsGrid.ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star)
            };
            Grid.SetColumn(ActionButtonsGrid, 0);
            Grid.SetRow(ActionButtonsGrid, 1);
            ActionButtonsGrid.WidthRequest = -1;
            ActionButtonsGrid.HorizontalOptions = LayoutOptions.Fill;

            ResultsCard.Padding = new Thickness(12);
            ResultsView.HeightRequest = 260;
            ResultsSubtitleLabel.IsVisible = false;
            ResultsHeaderGrid.ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star)
            };
            ResultsHeaderGrid.RowDefinitions = CreateAutoRows(2);
            MoveToGrid(ResultsCountBadge, 0, 1);
            ResultsCountBadge.HorizontalOptions = LayoutOptions.Start;
        }
        else
        {
            RootGrid.Padding = new Thickness(28, 24, 28, 32);
            RootGrid.RowSpacing = 18;
            PageTitleLabel.FontSize = 32;
            PageSubtitleLabel.IsVisible = true;
            HeaderGrid.ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star),
                new(GridLength.Auto)
            };
            HeaderBadge.IsVisible = true;

            IpInfoCard.Padding = new Thickness(22);
            MyIpLabel.FontSize = 22;
            MyCountryLabel.FontSize = 14;
            MyCountryLabel.MaxLines = 2;
            IpInfoCard.HeightRequest = -1;
            IpInfoCard.MinimumHeightRequest = -1;

            ScanSetupCard.Padding = new Thickness(20);
            ScanSetupGrid.RowSpacing = 18;
            ScanSubtitleLabel.IsVisible = true;
            ScanHeaderGrid.ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star),
                new(GridLength.Auto)
            };
            ScanModeBadge.IsVisible = true;

            AddressInputsGrid.RowDefinitions = new RowDefinitionCollection();
            AddressInputsGrid.ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star),
                new(GridLength.Star),
                new(new GridLength(0.55, GridUnitType.Star))
            };
            MoveToGrid(StartIpCard, 0, 0);
            MoveToGrid(EndIpCard, 1, 0);
            MoveToGrid(PortCard, 2, 0);
            SetInputCardPadding(14, 10);
            SetEntryHeight(36);

            OptionsInputsGrid.RowDefinitions = new RowDefinitionCollection();
            OptionsInputsGrid.ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star),
                new(GridLength.Star)
            };
            MoveToGrid(TimeoutCard, 0, 0);
            MoveToGrid(ConcurrencyCard, 1, 0);

            ScanControlsGrid.RowDefinitions = new RowDefinitionCollection();
            ScanControlsGrid.ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star),
                new(GridLength.Auto)
            };
            Grid.SetColumn(ActionButtonsGrid, 1);
            Grid.SetRow(ActionButtonsGrid, 0);
            ActionButtonsGrid.WidthRequest = 270;
            ActionButtonsGrid.HorizontalOptions = LayoutOptions.End;

            ResultsCard.Padding = new Thickness(18);
            ResultsView.HeightRequest = 360;
            ResultsSubtitleLabel.IsVisible = true;
            ResultsHeaderGrid.ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star),
                new(GridLength.Auto)
            };
            ResultsHeaderGrid.RowDefinitions = new RowDefinitionCollection();
            MoveToGrid(ResultsCountBadge, 1, 0);
            ResultsCountBadge.HorizontalOptions = LayoutOptions.End;
        }
    }

    private static RowDefinitionCollection CreateAutoRows(int count)
    {
        var rows = new RowDefinitionCollection();
        for (var i = 0; i < count; i++)
        {
            rows.Add(new RowDefinition(GridLength.Auto));
        }

        return rows;
    }

    private static void MoveToGrid(BindableObject view, int column, int row)
    {
        Grid.SetColumn(view, column);
        Grid.SetRow(view, row);
    }

    private void SetInputCardPadding(double horizontal, double vertical)
    {
        var padding = new Thickness(horizontal, vertical);
        StartIpCard.Padding = padding;
        EndIpCard.Padding = padding;
        PortCard.Padding = padding;
        TimeoutCard.Padding = padding;
        ConcurrencyCard.Padding = padding;
    }

    private void SetEntryHeight(double height)
    {
        StartIpEntry.HeightRequest = height;
        EndIpEntry.HeightRequest = height;
        PortEntry.HeightRequest = height;
        TimeoutEntry.HeightRequest = height;
        ConcurrencyEntry.HeightRequest = height;
    }

    private void ApplyDefaultValues()
    {
        StartIpEntry.Text = "188.114.96.0";
        EndIpEntry.Text = "188.114.99.255";
        PortEntry.Text = DefaultPort.ToString();
        TimeoutEntry.Text = DefaultTimeoutMs.ToString();
        ConcurrencyEntry.Text = NormalizeConcurrency(DefaultConcurrency).ToString();
    }

    private async Task UpdateIpInfoAsync()
    {
        try
        {
            await RunOnMainThreadAsync(() =>
            {
                MyIpLabel.Text = "Loading IP...";
                MyCountryLabel.Text = "Loading location...";
            });

            var info = await _ipInfoService.GetCurrentAsync();

            await RunOnMainThreadAsync(() =>
            {
                MyIpLabel.Text = $"IP: {info.IpAddress}";
                MyCountryLabel.Text = info.LocationText;
            });
        }
        catch
        {
            await RunOnMainThreadAsync(() =>
            {
                MyIpLabel.Text = "Connection error";
                MyCountryLabel.Text = "Location service is temporarily unavailable.";
            });
        }
    }

    private async Task SafeUpdateIpInfoAsync()
    {
        if (_isUpdatingIpInfo)
        {
            return;
        }

        _isUpdatingIpInfo = true;
        SetRefreshButtonState();

        try
        {
            await UpdateIpInfoAsync();
        }
        finally
        {
            _isUpdatingIpInfo = false;
            SetRefreshButtonState();
        }
    }
}

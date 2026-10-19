using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.App.Shell;

namespace RemoteShadow.App.Pages;

/// <summary>
/// Сбор полных сведений (ОС, модель, серийный номер, MAC, железо) об устройствах, найденных сканированием, по WinRM.
/// Запускается сам после скана, если включено <see cref="AppSettings.AutoCollectDeviceInfo"/>; работает в фоне, не мешает
/// ручным действиям, останавливается кнопкой «Пауза» и продолжается кнопкой «Продолжить» с того же места.
/// </summary>
public sealed partial class ComputersPageViewModel
{
    /// <summary>Сколько устройств опрашивается одновременно.</summary>
    public const int InventoryParallelism = 4;

    private IDeviceInventoryService? _inventory;
    private CancellationTokenSource? _enrichCts;

    [ObservableProperty]
    private bool _isEnriching;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanResumeEnrichment))]
    private bool _isEnrichPaused;

    [ObservableProperty]
    private string? _enrichStatus;

    [ObservableProperty]
    private DiscoveredDevice? _selectedDiscovered;

    /// <summary>Есть ли что собирать и работает ли сбор сейчас: для кнопок «Пауза» и «Продолжить».</summary>
    public bool CanResumeEnrichment => _inventory is not null && !IsEnriching && PendingInventoryKeys().Count > 0;

    /// <summary>Нужно ли собирать сведения по WinRM: только живые Windows-устройства (или с неопознанным типом), ещё не опрошенные.</summary>
    internal static bool NeedsInventory(DiscoveredDevice d) =>
        d.IsOnline && d.EnrichedUtc is null && d.EnrichError is null && (d.DeviceKind is null or "Windows");

    private List<string> PendingInventoryKeys() =>
        _discoveredByKey.Values.Where(NeedsInventory).Select(d => d.Key).ToList();

    partial void OnSelectedDiscoveredChanged(DiscoveredDevice? value) => OnPropertyChanged(nameof(SelectedDiscoveredText));

    /// <summary>Полные сведения о выбранном найденном устройстве, по строке на параметр.</summary>
    public string? SelectedDiscoveredText
    {
        get
        {
            var d = SelectedDiscovered;
            if (d is null) return null;

            var lines = new List<string>();
            void Add(string title, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) lines.Add($"{title}: {value}");
            }

            Add("Имя", d.Hostname is null ? null : d.NameSource is null ? d.Hostname : $"{d.Hostname} ({d.NameSource})");
            Add("IP-адрес", d.IpAddress);
            Add("Рабочая группа / домен", d.Workgroup);
            Add("MAC", d.Mac);
            Add("Производитель", d.Vendor);
            Add("Тип", d.DeviceKind);
            Add("ОС", d.OsName);
            Add("Версия ОС", d.OsVersion);
            Add("Оборудование", string.Join(" ", new[] { d.Manufacturer, d.Model }.Where(x => !string.IsNullOrWhiteSpace(x))));
            Add("Серийный номер", d.SerialNumber);
            Add("Служба", d.Banner);
            if (d.Details is not null)
            {
                foreach (var (key, value) in d.Details) Add(key, value);
            }

            Add("Сведения по WinRM", d.EnrichError is null ? null : $"не получены: {d.EnrichError}");
            if (d.MatchedComputerId is null) lines.Add("Не найден в AD");
            return string.Join(Environment.NewLine, lines);
        }
    }

    partial void OnIsEnrichingChanged(bool value) => OnPropertyChanged(nameof(CanResumeEnrichment));

    private void StartAutoEnrichmentIfEnabled()
    {
        if (_inventory is null || !_settingsStore.Load().AutoCollectDeviceInfo) return;
        _ = RunEnrichmentAsync();
    }

    [RelayCommand]
    private Task ResumeEnrichmentAsync() => RunEnrichmentAsync();

    [RelayCommand]
    private void PauseEnrichment()
    {
        if (!IsEnriching) return;
        IsEnrichPaused = true;
        _enrichCts?.Cancel();
    }

    /// <summary>Принудительно опрашивает выбранное устройство, в том числе то, где прошлая попытка закончилась ошибкой.</summary>
    [RelayCommand]
    private async Task CollectSelectedDeviceAsync()
    {
        var device = SelectedDiscovered;
        if (_inventory is null || device is null) return;

        var reset = device with { EnrichedUtc = null, EnrichError = null };
        StoreDiscovered(reset);
        await RunEnrichmentAsync(new List<string> { device.Key }).ConfigureAwait(true);
    }

    private async Task RunEnrichmentAsync(List<string>? keys = null)
    {
        if (_inventory is null) return;

        keys ??= PendingInventoryKeys();
        if (keys.Count == 0)
        {
            EnrichStatus = null;
            return;
        }

        _enrichCts?.Cancel();
        var cts = new CancellationTokenSource();
        _enrichCts = cts;
        IsEnriching = true;
        IsEnrichPaused = false;

        var total = keys.Count;
        var done = 0;
        EnrichStatus = $"Сбор сведений: 0 из {total}…";
        try
        {
            using var gate = new SemaphoreSlim(InventoryParallelism);
            var tasks = keys.Select(async key =>
            {
                await gate.WaitAsync(cts.Token).ConfigureAwait(true);
                try
                {
                    if (!_discoveredByKey.TryGetValue(key, out var device)) return;
                    var inventory = await _inventory.CollectAsync(device.ReverseDnsName ?? device.IpAddress, cts.Token).ConfigureAwait(true);
                    ApplyInventory(key, inventory);
                    EnrichStatus = $"Сбор сведений: {++done} из {total}…";
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(true);
            EnrichStatus = $"Сведения собраны: {done} из {total}.";
        }
        catch (OperationCanceledException)
        {
            EnrichStatus = IsEnrichPaused
                ? $"Сбор сведений приостановлен: {done} из {total}."
                : null;
        }
        catch (Exception ex)
        {
            EnrichStatus = null;
            _notifications.Error($"Не удалось собрать сведения: {ex.Message}");
        }
        finally
        {
            PersistDiscovered();
            if (ReferenceEquals(_enrichCts, cts))
            {
                IsEnriching = false;
                OnPropertyChanged(nameof(CanResumeEnrichment));
            }
        }
    }

    private void ApplyInventory(string key, DeviceInventory inventory)
    {
        if (!_discoveredByKey.TryGetValue(key, out var device)) return;

        DiscoveredDevice updated;
        if (inventory.Error is not null)
        {
            updated = device with { EnrichError = inventory.Error };
        }
        else
        {
            var gotName = device.Hostname is null && inventory.Hostname is not null;
            updated = device with
            {
                Hostname = device.Hostname ?? inventory.Hostname,
                NameSource = gotName ? "WMI" : device.NameSource,
                Workgroup = inventory.Domain ?? device.Workgroup,
                Mac = device.Mac ?? inventory.Mac,
                Vendor = device.Vendor ?? inventory.Manufacturer,
                DeviceKind = device.DeviceKind ?? "Windows",
                OsName = inventory.OsName,
                OsVersion = inventory.OsVersion,
                Manufacturer = inventory.Manufacturer,
                Model = inventory.Model,
                SerialNumber = inventory.SerialNumber,
                Details = inventory.Details,
                EnrichedUtc = DateTime.UtcNow,
                EnrichError = null,
            };
        }

        StoreDiscovered(updated with { MatchedComputerId = _deviceMerge.Match(updated, _index.All) });
    }

    private void LoadDiscoveredFromSettings()
    {
        var settings = _settingsStore.Load();
        var subnetIds = settings.Subnets.Select(s => s.Id).ToHashSet();
        foreach (var device in settings.DiscoveredDevices.Where(d => subnetIds.Contains(d.SubnetId)))
        {
            _discoveredByKey[device.Key] = device;
            DiscoveredDevices.Add(device);
        }
    }

    private void PersistDiscovered()
    {
        try
        {
            var settings = _settingsStore.Load();
            settings.DiscoveredDevices = _discoveredByKey.Values.ToList();
            _settingsStore.Save(settings);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Не удалось сохранить найденные устройства: {ex.Message}");
        }
    }

    /// <summary>Кладёт обновлённую запись в словарь и список, сохраняя выбор пользователя.</summary>
    private void StoreDiscovered(DiscoveredDevice device)
    {
        _discoveredByKey[device.Key] = device;
        var index = IndexOfDiscovered(device.Key);
        var wasSelected = SelectedDiscovered?.Key == device.Key;
        if (index >= 0) DiscoveredDevices[index] = device;
        else DiscoveredDevices.Add(device);
        if (wasSelected) SelectedDiscovered = device;
    }
}

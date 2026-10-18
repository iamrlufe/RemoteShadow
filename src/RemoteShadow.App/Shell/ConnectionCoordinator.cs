using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.App.Shell.Login;
using RemoteShadow.Core;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.App.Shell;

/// <summary>
/// Всё, что происходит с подключением к домену на уровне программы: тихий вход при запуске, окно входа,
/// смена учётной записи, выход и фоновая загрузка индекса компьютеров. Страницы пользуются готовым
/// <see cref="ICurrentConnection"/>, а смену учётной записи видят через его событие <c>Changed</c>.
/// </summary>
public sealed class ConnectionCoordinator
{
    private readonly IServiceProvider _services;
    private readonly ICurrentConnection _connection;
    private readonly IDomainLogonService _logon;
    private readonly IComputerIndex _index;
    private readonly ISessionCache _sessionCache;
    private readonly INotificationService _notifications;
    private CancellationTokenSource? _indexCts;
    private LogonResult? _autoLogonFailure;

    public ConnectionCoordinator(
        IServiceProvider services,
        ICurrentConnection connection,
        IDomainLogonService logon,
        IComputerIndex index,
        ISessionCache sessionCache,
        INotificationService notifications)
    {
        _services = services;
        _connection = connection;
        _logon = logon;
        _index = index;
        _sessionCache = sessionCache;
        _notifications = notifications;
    }

    /// <summary>Текст последней ошибки запуска индекса (не ошибки обновления — их хранит сам индекс). null, если ошибок нет.</summary>
    public string? IndexStartError { get; private set; }

    /// <summary>Меняется, когда запуск индекса завершился ошибкой или успешно перезапущен.</summary>
    public event EventHandler? IndexStartErrorChanged;

    /// <summary>
    /// Результат неудачного тихого входа: окно входа покажет причину (например, контроллер домена недоступен).
    /// Читается один раз; после отказа пользователя ждать (кнопка в окне подключения) причины нет.
    /// </summary>
    public LogonResult? TakeAutoLogonFailure()
    {
        var failure = _autoLogonFailure;
        _autoLogonFailure = null;
        return failure;
    }

    /// <summary>
    /// Тихий вход текущим пользователем: только если компьютер в домене, раньше входили так же и домен известен.
    /// На время входа показывается небольшое окно с возможностью отказаться. true — вход выполнен.
    /// </summary>
    public async Task<bool> TryAutoLogonAsync(AppSettings settings)
    {
        var environment = DetectEnvironment();
        if (!environment.IsDomainJoined || !settings.UseCurrentUser || string.IsNullOrWhiteSpace(settings.LastDomain))
            return false;

        using var cts = new CancellationTokenSource();
        var splash = new ConnectingWindow($"Домен {settings.LastDomain}, учётная запись {environment.CurrentUser}.");
        splash.Skipped += (_, _) => cts.Cancel();
        splash.Show();

        try
        {
            var request = new LogonRequest(
                LogonMode.CurrentUser,
                settings.LastDomain,
                string.IsNullOrWhiteSpace(settings.LastDomainController) ? null : settings.LastDomainController,
                null,
                null);

            var result = await _logon.LogonAsync(request, cts.Token);
            if (!result.Success || result.Connection is null)
            {
                if (!cts.IsCancellationRequested) _autoLogonFailure = result;
                return false;
            }

            Activate(result.Connection);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            CrashLog.Write("Тихий вход", ex);
            return false;
        }
        finally
        {
            splash.Close();
        }
    }

    /// <summary>
    /// Показывает окно входа. true — вход выполнен (подключение уже установлено и индекс запущен).
    /// <paramref name="exitsApp"/> — при запуске: кнопка «Выход» вместо «Отмена».
    /// </summary>
    public bool ShowLogin(Window? owner, bool exitsApp)
    {
        var viewModel = _services.GetRequiredService<LoginViewModel>();
        viewModel.ExitsApp = exitsApp;

        var window = new LoginWindow(viewModel);
        if (owner is { IsVisible: true }) window.Owner = owner;
        return window.ShowDialog() == true;
    }

    /// <summary>
    /// Делает подключение текущим: очищает индекс и кэш сессий прежней учётной записи, подставляет подключение
    /// (страницы получат <c>Changed</c> и перезагрузятся) и запускает загрузку индекса в фоне.
    /// </summary>
    public void Activate(IDomainConnection connection)
    {
        StopIndex();
        _index.Clear();
        _sessionCache.Clear();
        SetIndexStartError(null);

        _connection.Set(connection);

        var cts = new CancellationTokenSource();
        _indexCts = cts;
        _ = StartIndexAsync(cts.Token);
    }

    /// <summary>Выход: подключение освобождается, данные прежней учётной записи очищаются.</summary>
    public void SignOut()
    {
        StopIndex();
        _index.Clear();
        _sessionCache.Clear();
        SetIndexStartError(null);
        _connection.Set(null);
    }

    /// <summary>Останавливает фоновую работу при закрытии программы.</summary>
    public void Shutdown() => StopIndex();

    private DomainEnvironmentInfo DetectEnvironment()
    {
        try
        {
            return _logon.DetectEnvironment();
        }
        catch (Exception ex)
        {
            CrashLog.Write("Определение окружения", ex);
            return new DomainEnvironmentInfo(false, null, Environment.MachineName, Environment.UserName);
        }
    }

    private async Task StartIndexAsync(CancellationToken ct)
    {
        try
        {
            // Чтение кэша с диска может быть синхронным, поэтому уходим из потока интерфейса.
            await Task.Run(() => _index.InitializeAsync(ct), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            CrashLog.Write("Загрузка индекса компьютеров", ex);
            var message = ex is OperationFailedException
                ? ex.Message
                : $"Не удалось загрузить список компьютеров: {ex.Message}";
            SetIndexStartError(message);
            _notifications.Error(message);
        }
    }

    private void StopIndex()
    {
        var cts = _indexCts;
        _indexCts = null;
        if (cts is null) return;

        try
        {
            cts.Cancel();
        }
        finally
        {
            cts.Dispose();
        }
    }

    private void SetIndexStartError(string? message)
    {
        IndexStartError = message;
        UiThread.Post(() => IndexStartErrorChanged?.Invoke(this, EventArgs.Empty));
    }
}

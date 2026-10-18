using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteShadow.Core;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Core.Util;

namespace RemoteShadow.App.Shell.Login;

/// <summary>
/// Окно входа: домен, контроллер домена, логин и пароль либо текущий пользователь Windows.
/// Пароль в модели не хранится: окно передаёт делегаты чтения и очистки <c>PasswordBox</c>, а пароль читается
/// только в момент нажатия «Войти» и очищается после попытки.
/// </summary>
public sealed partial class LoginViewModel : ObservableObject
{
    private readonly IDomainLogonService _logon;
    private readonly ISettingsStore _settingsStore;
    private readonly ICredentialStore _credentialStore;
    private readonly ConnectionCoordinator _coordinator;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private CancellationTokenSource? _cts;

    public LoginViewModel(
        IDomainLogonService logon,
        ISettingsStore settingsStore,
        ICredentialStore credentialStore,
        ConnectionCoordinator coordinator,
        IDialogService dialogs,
        INotificationService notifications,
        StartupOptions options)
    {
        _logon = logon;
        _settingsStore = settingsStore;
        _credentialStore = credentialStore;
        _coordinator = coordinator;
        _dialogs = dialogs;
        _notifications = notifications;
        IsDemo = options.IsDemo;

        var environment = DetectEnvironment();
        CanUseCurrentUser = environment.IsDomainJoined;
        CurrentUserLabel = string.IsNullOrWhiteSpace(environment.CurrentUser) ? "учётная запись Windows" : environment.CurrentUser;

        var settings = LoadSettings();
        _isCurrentUserMode = CanUseCurrentUser && settings.UseCurrentUser;
        _domain = settings.LastDomain ?? environment.DomainName ?? string.Empty;
        _domainController = settings.LastDomainController ?? string.Empty;
        _userName = settings.LastUserName ?? string.Empty;

        // Тихий вход при запуске не удался: сразу объясняем причину, а не показываем пустую форму.
        if (coordinator.TakeAutoLogonFailure() is { } failure) ShowLogonFailure(failure);
    }

    /// <summary>Подсказка для демо-режима.</summary>
    public bool IsDemo { get; }

    public string DemoHint => "Демо: логин любой, пароль demo (da-... — привилегированная, locked — заблокирована).";

    /// <summary>Компьютер в домене: можно войти текущим пользователем.</summary>
    public bool CanUseCurrentUser { get; }

    /// <summary>Текущий пользователь Windows, например CORP\ivanov.</summary>
    public string CurrentUserLabel { get; }

    /// <summary>При запуске закрытие окна завершает программу, при смене учётной записи — просто отменяет.</summary>
    public bool ExitsApp
    {
        get => _exitsApp;
        set
        {
            if (SetProperty(ref _exitsApp, value)) OnPropertyChanged(nameof(CloseButtonText));
        }
    }

    private bool _exitsApp;

    /// <summary>Во время входа кнопка отменяет вход; иначе при запуске закрывает программу, при смене учётной записи — окно.</summary>
    public string CloseButtonText => !IsBusy && ExitsApp ? "Выход" : "Отмена";

    /// <summary>Окно задаёт: прочитать пароль из PasswordBox.</summary>
    public Func<string>? PasswordProvider { get; set; }

    /// <summary>Окно задаёт: очистить PasswordBox.</summary>
    public Action? PasswordClearer { get; set; }

    /// <summary>Окно задаёт: закрыть себя с результатом (true — вход выполнен).</summary>
    public Action<bool>? CloseRequested { get; set; }

    /// <summary>Попросить окно поставить фокус в поле «Контроллер домена».</summary>
    public event EventHandler? FocusDomainControllerRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExplicitMode))]
    private bool _isCurrentUserMode;

    /// <summary>«Другая учётная запись». Парная к <see cref="IsCurrentUserMode"/> для второй радиокнопки.</summary>
    public bool IsExplicitMode
    {
        get => !IsCurrentUserMode;
        set
        {
            if (value) IsCurrentUserMode = false;
        }
    }

    [ObservableProperty]
    private string _domain;

    [ObservableProperty]
    private string _domainController;

    [ObservableProperty]
    private string _userName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRememberPassword))]
    private bool _rememberLogin = true;

    [ObservableProperty]
    private bool _rememberPassword;

    /// <summary>Пароль можно сохранить только вместе с логином.</summary>
    public bool CanRememberPassword => RememberLogin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    [NotifyPropertyChangedFor(nameof(CloseButtonText))]
    private bool _isBusy;

    public bool IsNotBusy => !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorTitle = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorHint))]
    private string _errorHint = string.Empty;

    [ObservableProperty]
    private bool _canChangeController;

    public bool HasError => ErrorTitle.Length > 0;

    public bool HasErrorHint => ErrorHint.Length > 0;

    partial void OnRememberLoginChanged(bool value)
    {
        if (!value) RememberPassword = false;
    }

    /// <summary>
    /// Подставляет сохранённые данные (логин, домен, контроллер) и возвращает сохранённый пароль, если он есть:
    /// окно кладёт его в PasswordBox, в модели он не остаётся.
    /// </summary>
    public string? LoadStored()
    {
        StoredLogon? stored;
        try
        {
            stored = _credentialStore.Load();
        }
        catch (Exception ex)
        {
            CrashLog.Write("Чтение сохранённого входа", ex);
            return null;
        }

        if (stored is null) return null;

        if (!string.IsNullOrWhiteSpace(stored.Domain)) Domain = stored.Domain;
        if (!string.IsNullOrWhiteSpace(stored.DomainController)) DomainController = stored.DomainController;
        if (!string.IsNullOrWhiteSpace(stored.UserName))
        {
            UserName = stored.UserName;
            RememberLogin = true;
        }

        if (string.IsNullOrEmpty(stored.Password)) return null;

        RememberPassword = true;
        return stored.Password;
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (IsBusy) return;
        ClearError();

        var domain = Domain.Trim();
        var controller = DomainController.Trim();
        var login = UserName.Trim();
        string? password = null;

        if (IsExplicitMode)
        {
            if (login.Length == 0)
            {
                ShowError("Введите логин", "Логин можно указать как CORP\\ivanov или ivanov@corp.local.", null, false);
                return;
            }

            password = PasswordProvider?.Invoke() ?? string.Empty;
            if (password.Length == 0)
            {
                ShowError("Введите пароль", null, null, false);
                return;
            }

            if (domain.Length == 0 && AccountNameParser.Parse(login).UpnSuffix is { } upnDomain) domain = upnDomain;
        }

        if (domain.Length == 0)
        {
            var (title, hint) = LoginErrorTexts.Describe(LogonError.DomainNotSpecified);
            ShowError(title, hint, null, false);
            return;
        }

        var request = new LogonRequest(
            IsCurrentUserMode ? LogonMode.CurrentUser : LogonMode.Explicit,
            domain,
            controller.Length == 0 ? null : controller,
            IsExplicitMode ? login : null,
            password);

        var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;

        LogonResult? result = null;
        try
        {
            result = await _logon.LogonAsync(request, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Пользователь отменил вход: сообщать нечего.
        }
        catch (Exception ex)
        {
            CrashLog.Write("Вход в домен", ex);
            ShowError("Не удалось выполнить вход", ex is OperationFailedException ? ex.Message : "Непредвиденная ошибка. Подробности записаны в crash.log.", null, false);
        }
        finally
        {
            _cts = null;
            cts.Dispose();
            IsBusy = false;
            PasswordClearer?.Invoke();
        }

        if (result is null) return;

        if (!result.Success || result.Connection is null)
        {
            ShowLogonFailure(result);
            return;
        }

        await CompleteAsync(result, domain, controller, login, password);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy) _cts?.Cancel();
        else CloseRequested?.Invoke(false);
    }

    [RelayCommand]
    private void ChangeController() => FocusDomainControllerRequested?.Invoke(this, EventArgs.Empty);

    private async Task CompleteAsync(LogonResult result, string domain, string controller, string login, string? password)
    {
        _coordinator.Activate(result.Connection!);

        var savePassword = RememberPassword && RememberLogin && !result.IsPrivilegedAccount && !string.IsNullOrEmpty(password);
        SavePreferences(domain, controller, login, savePassword ? password : null);

        if (result.IsPrivilegedAccount)
        {
            await _dialogs.ShowInfoAsync(
                "Привилегированная учётная запись",
                "Эта учётная запись входит в привилегированную группу (Domain Admins, Enterprise Admins или Administrators)."
                + Environment.NewLine + Environment.NewLine
                + "Пароль не сохранён в Диспетчере учётных данных Windows. Для повседневной работы рекомендуется отдельная "
                + "учётная запись с делегированными правами: управление пользователями в нужных подразделениях и локальный "
                + "администратор на рабочих станциях через групповую политику.");
        }

        CloseRequested?.Invoke(true);
    }

    private void SavePreferences(string domain, string controller, string login, string? passwordToStore)
    {
        try
        {
            var settings = _settingsStore.Load();
            settings.UseCurrentUser = IsCurrentUserMode;
            settings.LastDomain = domain;
            settings.LastDomainController = controller.Length == 0 ? null : controller;
            if (IsExplicitMode) settings.LastUserName = RememberLogin ? login : null;
            _settingsStore.Save(settings);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Сохранение настроек входа", ex);
            _notifications.Error($"Не удалось сохранить настройки входа: {ex.Message}");
        }

        if (!IsExplicitMode) return;

        try
        {
            if (RememberLogin)
                _credentialStore.Save(new StoredLogon(domain, controller.Length == 0 ? null : controller, login, passwordToStore));
            else
                _credentialStore.Delete();
        }
        catch (Exception ex)
        {
            CrashLog.Write("Сохранение учётных данных", ex);
            _notifications.Error($"Не удалось сохранить учётные данные: {ex.Message}");
        }
    }

    private void ShowLogonFailure(LogonResult result)
    {
        var (title, hint) = LoginErrorTexts.Describe(result.Error);
        var message = string.IsNullOrWhiteSpace(result.Message) ? string.Empty : result.Message!;
        ShowError(title, hint, message, result.Error == LogonError.DomainControllerUnreachable);
    }

    private void ShowError(string title, string? hint, string? message, bool canChangeController)
    {
        ErrorTitle = title;
        ErrorHint = hint ?? string.Empty;
        ErrorMessage = message ?? string.Empty;
        CanChangeController = canChangeController;
    }

    private void ClearError() => ShowError(string.Empty, null, null, false);

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

    private AppSettings LoadSettings()
    {
        try
        {
            return _settingsStore.Load();
        }
        catch (Exception ex)
        {
            CrashLog.Write("Чтение настроек", ex);
            return new AppSettings();
        }
    }
}

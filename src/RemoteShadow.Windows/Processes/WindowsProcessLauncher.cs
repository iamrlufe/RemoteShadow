using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RemoteShadow.Core;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Windows.Native;

namespace RemoteShadow.Windows;

/// <summary>
/// Запуск внешних программ (mstsc, ssh, explorer) от имени текущего подключения. Без cmd /c, только со списком аргументов.
/// Текущий пользователь: Process.Start с ArgumentList. Явные учётные данные: CreateProcessWithLogonW с
/// LOGON_NETCREDENTIALS_ONLY (аналог runas /netonly), для чего нужна служба «Вторичный вход в систему» (seclogon).
/// Завершения процесса не ждёт.
/// </summary>
public sealed class WindowsProcessLauncher : IProcessLauncher
{
    private readonly ICurrentConnection _current;

    public WindowsProcessLauncher(ICurrentConnection current)
    {
        _current = current;
    }

    public async Task StartAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ct.ThrowIfCancellationRequested();

        var path = FindExecutable(fileName)
                   ?? throw new OperationFailedException(
                       $"Не найдена программа «{fileName}». Проверьте, что она установлена и доступна в PATH.",
                       OperationFailure.NotFound);

        foreach (var argument in arguments)
        {
            if (argument is null || argument.Contains('\0'))
                throw new OperationFailedException("Недопустимый аргумент командной строки.", OperationFailure.InvalidInput);
        }

        var credential = _current.Current?.Credential;
        await Task.Run(
            () =>
            {
                if (credential is null)
                    StartAsCurrentUser(path, arguments);
                else
                    StartWithNetworkCredentials(path, arguments, credential);
            },
            ct).ConfigureAwait(false);
    }

    public string? FindExecutable(string fileName) =>
        ExecutableLocator.Find(fileName, ExecutableLocator.DefaultSearchDirectories(), File.Exists);

    private static void StartAsCurrentUser(string path, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(path) { UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info);
        }
        catch (Win32Exception ex)
        {
            throw ProcessLaunchErrors.FromWin32(ex.NativeErrorCode, Path.GetFileName(path), ex.Message, ex);
        }
    }

    private static unsafe void StartWithNetworkCredentials(string path, IReadOnlyList<string> arguments, AccountCredential credential)
    {
        var commandLine = CommandLineBuilder.Build(path, arguments);
        if (commandLine.Length >= CommandLineBuilder.MaxLength)
        {
            throw new OperationFailedException(
                "Слишком длинная командная строка для запуска под другой учётной записью (больше 1024 символов).",
                OperationFailure.InvalidInput);
        }

        // CreateProcessWithLogonW может изменять строку на месте, поэтому ей нужен записываемый буфер.
        var buffer = new char[commandLine.Length + 1];
        commandLine.CopyTo(0, buffer, 0, commandLine.Length);

        // Для UPN домен должен быть null.
        var domain = credential.UserName.Contains('@') || string.IsNullOrEmpty(credential.Domain) ? null : credential.Domain;
        var startup = new StartupInfoW { Cb = sizeof(StartupInfoW) };

        bool started;
        ProcessInformation process;
        fixed (char* pCommandLine = buffer)
        {
            started = NativeMethods.CreateProcessWithLogon(
                credential.UserName,
                domain,
                credential.Password,
                NativeMethods.LogonNetCredentialsOnly,
                path,
                pCommandLine,
                0,
                IntPtr.Zero,
                Environment.SystemDirectory,
                in startup,
                out process);
        }

        if (!started)
        {
            var code = Marshal.GetLastPInvokeError();
            throw ProcessLaunchErrors.FromWin32(code, Path.GetFileName(path), new Win32Exception(code).Message);
        }

        if (process.Thread != IntPtr.Zero) NativeMethods.CloseHandle(process.Thread);
        if (process.Process != IntPtr.Zero) NativeMethods.CloseHandle(process.Process);
    }
}

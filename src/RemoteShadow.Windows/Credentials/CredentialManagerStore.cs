using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RemoteShadow.Core;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Windows.Native;

namespace RemoteShadow.Windows;

/// <summary>
/// Хранилище входа в Диспетчере учётных данных Windows (CredWriteW / CredReadW / CredDeleteW), цель «RemoteShadow:login»,
/// тип CRED_TYPE_GENERIC, CRED_PERSIST_LOCAL_MACHINE. Windows шифрует запись (DPAPI) под учётной записью пользователя.
/// Пароль пишется только если он есть в <see cref="StoredLogon.Password"/>. Формат полей — в <see cref="CredentialRecordCodec"/>.
/// </summary>
public sealed class CredentialManagerStore : ICredentialStore
{
    /// <summary>Читает запись. Нет записи или она не читается — null: тогда окно входа просто откроется пустым.</summary>
    public unsafe StoredLogon? Load()
    {
        IntPtr pointer;
        try
        {
            if (!NativeMethods.CredRead(CredentialRecordCodec.TargetName, NativeMethods.CredTypeGeneric, 0, out pointer))
                return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<CredentialW>(pointer);
            var rawUserName = Marshal.PtrToStringUni(credential.UserName);
            if (string.IsNullOrEmpty(rawUserName)) return null;

            var (userDomain, userName) = CredentialRecordCodec.ParseUserName(rawUserName);
            var (commentDomain, controller) = CredentialRecordCodec.ParseComment(Marshal.PtrToStringUni(credential.Comment));

            string? password = null;
            if (credential.CredentialBlob != IntPtr.Zero && credential.CredentialBlobSize > 0)
            {
                password = CredentialRecordCodec.DecodePassword(
                    new ReadOnlySpan<byte>(credential.CredentialBlob.ToPointer(), (int)credential.CredentialBlobSize));
            }

            return new StoredLogon(commentDomain ?? userDomain ?? string.Empty, controller, userName, password);
        }
        finally
        {
            NativeMethods.CredFree(pointer);
        }
    }

    public unsafe void Save(StoredLogon logon)
    {
        ArgumentNullException.ThrowIfNull(logon);

        var userName = CredentialRecordCodec.BuildUserName(logon.Domain, logon.UserName);
        var comment = CredentialRecordCodec.BuildComment(logon.Domain, logon.DomainController);
        if (userName.Length > CredentialRecordCodec.MaxUserNameLength || comment.Length > CredentialRecordCodec.MaxCommentLength)
        {
            throw new OperationFailedException(
                "Не удалось сохранить вход: слишком длинное имя домена, контроллера или логина.",
                OperationFailure.InvalidInput);
        }

        var blob = logon.Password is { Length: > 0 } password ? CredentialRecordCodec.EncodePassword(password) : null;
        try
        {
            if (blob is not null && blob.Length > CredentialRecordCodec.MaxBlobSize)
            {
                throw new OperationFailedException(
                    "Не удалось сохранить пароль: он слишком длинный для Диспетчера учётных данных Windows.",
                    OperationFailure.InvalidInput);
            }

            bool written;
            int code;
            fixed (char* pTarget = CredentialRecordCodec.TargetName)
            fixed (char* pComment = comment)
            fixed (char* pUser = userName)
            fixed (byte* pBlob = blob)
            {
                var credential = new CredentialW
                {
                    Type = NativeMethods.CredTypeGeneric,
                    TargetName = (IntPtr)pTarget,
                    Comment = (IntPtr)pComment,
                    CredentialBlobSize = (uint)(blob?.Length ?? 0),
                    CredentialBlob = (IntPtr)pBlob,
                    Persist = NativeMethods.CredPersistLocalMachine,
                    UserName = (IntPtr)pUser,
                };

                written = NativeMethods.CredWrite(in credential, 0);
                code = written ? 0 : Marshal.GetLastPInvokeError();
            }

            if (!written) throw StoreFailure("сохранить вход", code);
        }
        finally
        {
            if (blob is not null) CryptographicOperations.ZeroMemory(blob);
        }
    }

    /// <summary>Удаляет запись. Если её нет — ничего не делает.</summary>
    public void Delete()
    {
        if (NativeMethods.CredDelete(CredentialRecordCodec.TargetName, NativeMethods.CredTypeGeneric, 0)) return;

        var code = Marshal.GetLastPInvokeError();
        if (code == Win32Errors.NotFound) return;
        throw StoreFailure("удалить сохранённый вход", code);
    }

    private static OperationFailedException StoreFailure(string action, int code) =>
        new(
            $"Не удалось {action} в Диспетчере учётных данных Windows: {new Win32Exception(code).Message} (код {code}).",
            OperationFailure.Other);
}

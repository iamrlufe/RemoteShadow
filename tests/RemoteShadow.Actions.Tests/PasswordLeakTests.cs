using System.Collections;
using System.Reflection;
using RemoteShadow.Core;
using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Modules;

namespace RemoteShadow.Actions.Tests;

/// <summary>Новый пароль живёт только в параметрах действия и уходит только в каталог пользователей.</summary>
public class PasswordLeakTests
{
    private const string Secret = "Zq9#уникальный-Пароль-7731";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Successful_reset_does_not_leak_password(bool mustChange)
    {
        var env = new TestEnv();
        env.Dialogs.PasswordAnswer = new PasswordChoice(Secret, mustChange);
        var action = new ResetPasswordAction();
        var target = Samples.UserTarget();

        var parameters = await action.PrepareAsync([target], env.Context(), CancellationToken.None);
        var ctx = env.Context(parameters, reason: "Забыл пароль");
        var result = await action.ExecuteAsync(target, ctx, CancellationToken.None);

        // Каталогу пароль достаётся — иначе сброс бессмыслен.
        Assert.Equal(Secret, Assert.Single(env.Users.PasswordResets).Password);

        AssertClean(result);
        AssertClean(ctx.AuditDetails);
        AssertClean(ctx.Reason);
        AssertClean(action);
        AssertClean(env.Dialogs.PasswordPrompts);
        AssertClean(env.Dialogs.TextPrompts);
        AssertClean(env.Dialogs.Errors);
        AssertClean(env.Dialogs.Infos);
        AssertClean(env.Launcher.Launches);
        AssertClean(env.Sessions.Messages);
        AssertClean(env.Diagnostics.Fixes);
    }

    [Fact]
    public async Task Directory_failures_do_not_pick_up_the_password()
    {
        foreach (var failure in new Exception[]
                 {
                     new OperationFailedException("Недостаточно прав для сброса пароля.", OperationFailure.AccessDenied),
                     new InvalidOperationException("boom"),
                     new TimeoutException(),
                 })
        {
            var env = new TestEnv();
            env.Users.ResetPasswordFailure = failure;
            var ctx = env.With(("password", Secret), ("mustChange", "true"));

            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                new ResetPasswordAction().ExecuteAsync(Samples.UserTarget(), ctx, CancellationToken.None));

            Assert.Same(failure, ex);
            AssertClean(ex.ToString());
            AssertClean(ex.Data);
            AssertClean(ctx.AuditDetails);
        }
    }

    [Fact]
    public async Task Validation_errors_do_not_contain_the_password()
    {
        var env = new TestEnv();

        // Параметр пустой: в ошибке не должно быть ни пароля, ни DN, ни значений параметров.
        var missing = await Assert.ThrowsAsync<OperationFailedException>(() =>
            new ResetPasswordAction().ExecuteAsync(Samples.UserTarget(), env.With(("password", ""), ("mustChange", Secret)), CancellationToken.None));
        AssertClean(missing.ToString());

        // Пустой пароль из диалога.
        env.Dialogs.PasswordAnswer = new PasswordChoice("", true);
        var empty = await Assert.ThrowsAsync<OperationFailedException>(() =>
            new ResetPasswordAction().PrepareAsync([Samples.UserTarget()], env.Context(), CancellationToken.None));
        AssertClean(empty.ToString());
    }

    [Fact]
    public async Task Cancelled_prepare_leaves_nothing_behind()
    {
        var env = new TestEnv();
        env.Dialogs.PasswordAnswer = null;

        Assert.Null(await new ResetPasswordAction().PrepareAsync([Samples.UserTarget()], env.Context(), CancellationToken.None));
        Assert.Empty(env.Users.PasswordResets);
    }

    /// <summary>Обходит строки и объекты рефлексией (свойства и поля) и падает, если нашёл пароль.</summary>
    private static void AssertClean(object? root) => Walk(root, "root", new HashSet<object>(ReferenceEqualityComparer.Instance), 0);

    private static void Walk(object? value, string path, HashSet<object> visited, int depth)
    {
        if (value is null || depth > 6) return;

        if (value is string s)
        {
            Assert.False(s.Contains(Secret, StringComparison.Ordinal), $"Пароль найден в {path}");
            return;
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is Type || value is Delegate || value is MemberInfo) return;
        if (!type.IsValueType && !visited.Add(value)) return;

        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                Walk(entry.Key, path + ".Key", visited, depth + 1);
                Walk(entry.Value, path + ".Value", visited, depth + 1);
            }

            return;
        }

        if (value is IEnumerable items)
        {
            var i = 0;
            foreach (var item in items) Walk(item, $"{path}[{i++}]", visited, depth + 1);
            return;
        }

        const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var property in type.GetProperties(all).Where(p => p.GetIndexParameters().Length == 0))
        {
            object? child;
            try
            {
                child = property.GetValue(value);
            }
            catch
            {
                continue;
            }

            Walk(child, $"{path}.{property.Name}", visited, depth + 1);
        }

        foreach (var field in type.GetFields(all))
            Walk(field.GetValue(value), $"{path}.{field.Name}", visited, depth + 1);
    }
}

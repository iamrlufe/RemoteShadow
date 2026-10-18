using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Actions.Tests;

/// <summary>Диалоги с программируемыми ответами. Пустая очередь ответов = пользователь нажал «Отмена».</summary>
public sealed class FakeDialogService : IDialogService
{
    public sealed record TextPrompt(string Title, string Message, string? Initial, bool Multiline);

    /// <summary>Ответы на <see cref="PromptTextAsync"/> по порядку; null — отмена.</summary>
    public Queue<string?> TextAnswers { get; } = new();

    public List<TextPrompt> TextPrompts { get; } = new();

    public PasswordChoice? PasswordAnswer { get; set; }
    public List<string> PasswordPrompts { get; } = new();

    public GroupInfo? PickAnswer { get; set; }
    public string? PickTitle { get; private set; }

    /// <summary>Функция поиска, которую действие передало диалогу выбора группы.</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<GroupInfo>>>? PickSearch { get; private set; }

    public List<(string Title, string Message)> Errors { get; } = new();
    public List<(string Title, string Message)> Infos { get; } = new();

    public Task<bool> ConfirmAsync(ConfirmRequest request) =>
        throw new NotSupportedException("Подтверждение — дело оболочки, действия его не запрашивают.");

    public Task<ReasonResult> ConfirmWithReasonAsync(ConfirmRequest request) =>
        throw new NotSupportedException("Подтверждение — дело оболочки, действия его не запрашивают.");

    public Task<string?> PromptTextAsync(string title, string message, string? initial = null, bool multiline = false)
    {
        TextPrompts.Add(new TextPrompt(title, message, initial, multiline));
        return Task.FromResult(TextAnswers.Count > 0 ? TextAnswers.Dequeue() : null);
    }

    public Task<PasswordChoice?> PromptPasswordResetAsync(string userLabel)
    {
        PasswordPrompts.Add(userLabel);
        return Task.FromResult(PasswordAnswer);
    }

    public Task<GroupInfo?> PickGroupAsync(string title, Func<string, CancellationToken, Task<IReadOnlyList<GroupInfo>>> search)
    {
        PickTitle = title;
        PickSearch = search;
        return Task.FromResult(PickAnswer);
    }

    public Task ShowErrorAsync(string title, string message)
    {
        Errors.Add((title, message));
        return Task.CompletedTask;
    }

    public Task ShowInfoAsync(string title, string message)
    {
        Infos.Add((title, message));
        return Task.CompletedTask;
    }
}

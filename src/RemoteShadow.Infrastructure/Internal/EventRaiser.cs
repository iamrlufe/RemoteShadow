namespace RemoteShadow.Infrastructure;

internal static class EventRaiser
{
    /// <summary>
    /// Вызывает подписчиков по одному. Исключение подписчика (например, интерфейс обратился к элементу не из своего потока)
    /// не должно прерывать загрузку индекса или опрос компьютеров, поэтому глотается.
    /// </summary>
    public static void Raise(EventHandler? handler, object sender)
    {
        if (handler is null) return;

        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler)subscriber)(sender, EventArgs.Empty);
            }
            catch
            {
                // Ошибка подписчика — не наша.
            }
        }
    }
}

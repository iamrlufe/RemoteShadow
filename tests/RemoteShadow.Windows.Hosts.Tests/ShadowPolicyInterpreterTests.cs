using RemoteShadow.Core.Models;

namespace RemoteShadow.Windows.Hosts.Tests;

public class ShadowPolicyInterpreterTests
{
    [Fact]
    public void Not_set_behaves_like_full_control_with_consent()
    {
        var o = ShadowPolicyInterpreter.Interpret(null);

        Assert.Equal(DiagnosticStatus.Ok, o.Status);
        Assert.Contains("не задана", o.Detail);
        Assert.NotNull(o.Hint);
        Assert.Contains("запрос", o.Hint);
        Assert.Contains("2", o.Hint);
        Assert.Contains("4", o.Hint);
    }

    [Fact]
    public void Zero_forbids_shadow_and_points_to_policy_location()
    {
        var o = ShadowPolicyInterpreter.Interpret(0);

        Assert.Equal(DiagnosticStatus.Fail, o.Status);
        Assert.Contains("Shadow запрещён политикой", o.Detail);
        Assert.NotNull(o.Hint);
        Assert.Contains("Конфигурация компьютера → Административные шаблоны → Компоненты Windows → Службы удалённых рабочих столов", o.Hint);
        Assert.Contains("Задать правила удалённого управления сеансами пользователей RDS", o.Hint);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(3u)]
    public void Consent_values_are_ok_but_warn_that_user_gets_a_prompt(uint value)
    {
        var o = ShadowPolicyInterpreter.Interpret(value);

        Assert.Equal(DiagnosticStatus.Ok, o.Status);
        Assert.Contains($"Shadow = {value}", o.Detail);
        Assert.NotNull(o.Hint);
        Assert.Contains("Пользователю придёт запрос", o.Hint);
        Assert.Contains("значение политики 2", o.Hint);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(4u)]
    public void No_consent_values_are_ok(uint value)
    {
        var o = ShadowPolicyInterpreter.Interpret(value);

        Assert.Equal(DiagnosticStatus.Ok, o.Status);
        Assert.Contains($"Shadow = {value}", o.Detail);
        Assert.Contains("без запроса", o.Detail);
        Assert.DoesNotContain("Пользователю придёт запрос", o.Hint ?? string.Empty);
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(4u)]
    public void View_only_values_say_control_is_unavailable(uint value)
    {
        var o = ShadowPolicyInterpreter.Interpret(value);

        Assert.Contains("только просмотр", o.Detail);
        Assert.Contains("режим управления недоступен", o.Hint);
    }

    [Theory]
    [InlineData(5u)]
    [InlineData(99u)]
    [InlineData(uint.MaxValue)]
    public void Unexpected_values_are_a_warning(uint value)
    {
        var o = ShadowPolicyInterpreter.Interpret(value);

        Assert.Equal(DiagnosticStatus.Warning, o.Status);
        Assert.Contains(value.ToString(), o.Detail);
    }

    [Fact]
    public void Policy_item_never_offers_a_fix_because_gpo_owns_the_value()
    {
        foreach (uint? value in new uint?[] { null, 0, 1, 2, 3, 4, 7 })
        {
            var item = DiagnosticItems.ShadowPolicy(value);
            Assert.Equal(DiagnosticIds.ShadowPolicy, item.Id);
            Assert.Null(item.FixId);
        }
    }
}

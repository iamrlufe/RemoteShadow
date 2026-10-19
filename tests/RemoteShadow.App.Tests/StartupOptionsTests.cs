using RemoteShadow.App.Shell;

namespace RemoteShadow.App.Tests;

/// <summary>Ключи запуска: --demo, --login, --domain/--dc/--user (подстановка в окно входа) и --profile (отдельная папка данных).</summary>
public sealed class StartupOptionsTests
{
    private static StartupOptions Parse(params string[] args) => StartupOptions.Parse(args, _ => null);

    [Fact]
    public void No_arguments_means_silent_logon_in_the_default_profile()
    {
        var o = Parse();

        Assert.False(o.IsDemo);
        Assert.False(o.PromptsForLogin);
        Assert.Null(o.Profile);
    }

    [Fact]
    public void Demo_flag_and_environment_variable_both_enable_demo()
    {
        Assert.True(Parse("--demo").IsDemo);
        Assert.True(StartupOptions.Parse([], name => name == "REMOTESHADOW_DEMO" ? "1" : null).IsDemo);
    }

    [Fact]
    public void Login_flag_forces_the_login_window()
    {
        Assert.True(Parse("--login").PromptsForLogin);
    }

    [Theory]
    [InlineData("--domain", "example.org")]
    [InlineData("--domain=example.org")]
    public void Domain_is_read_in_both_forms_and_implies_login(params string[] args)
    {
        var o = Parse(args);

        Assert.Equal("example.org", o.Domain);
        Assert.True(o.PromptsForLogin);
    }

    [Fact]
    public void Domain_controller_and_user_are_read_and_quotes_are_trimmed()
    {
        var o = Parse("--dc", "dc.example.org", "--user=\"EXAMPLE\\user\"");

        Assert.Equal("dc.example.org", o.DomainController);
        Assert.Equal("EXAMPLE\\user", o.UserName);
    }

    [Fact]
    public void A_key_without_a_value_does_not_swallow_the_next_key()
    {
        var o = Parse("--domain", "--login");

        Assert.Null(o.Domain);
        Assert.True(o.ForceLogin);
    }

    [Theory]
    [InlineData("work", "work")]
    [InlineData("..\\evil", "evil")]
    [InlineData("a/b c", "abc")]
    [InlineData("***", null)]
    public void Profile_name_is_reduced_to_safe_folder_characters(string given, string? expected)
    {
        Assert.Equal(expected, Parse("--profile", given).Profile);
    }
}

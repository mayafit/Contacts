using ContactsApi.WebApi.Services;

namespace ContactsApi.Tests;

/// <summary>
/// Tests for the operator/admin allowlist (Epic 22). Like <see cref="AllowedEmails"/>
/// it is fail-closed and parses the same separators, but membership is the whole
/// check — the email already comes from a verified, allowlisted session.
/// </summary>
public class AdminEmailsTests
{
    [Fact]
    public void Entries_are_split_on_commas_semicolons_and_spaces_and_a_rendered_vault_list_works()
    {
        var list = new AdminEmails("[A@x.com b@y.com],c@z.com;  D@W.org ");

        Assert.Equal(4, list.Count);
        Assert.True(list.IsConfigured);
        Assert.True(list.IsAdmin("a@X.COM"));
        Assert.True(list.IsAdmin("d@w.org"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ;, ")]
    [InlineData("not-an-email")]
    public void An_empty_or_invalid_list_makes_nobody_an_admin(string? configured)
    {
        var list = new AdminEmails(configured);

        Assert.False(list.IsConfigured);
        Assert.False(list.IsAdmin("a@x.com"));
    }

    [Fact]
    public void A_non_admin_address_is_rejected()
    {
        var list = new AdminEmails("owner@x.com");

        Assert.True(list.IsAdmin("owner@x.com"));
        Assert.False(list.IsAdmin("family@x.com"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_email_is_never_an_admin(string? email)
    {
        Assert.False(new AdminEmails("owner@x.com").IsAdmin(email));
    }
}

using ContactsApi.Infrastructure.Tenancy;

namespace ContactsApi.Tests;

/// <summary>The owner scope and the connection string guards (AD-49 rule 4), without a database</summary>
public class OwnerScopeTests
{
    [Theory]
    [InlineData("109876543210987654321", true)]
    [InlineData("owner-a_1", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("quote'", false)]
    [InlineData("dots.not.allowed", false)]
    public void Owner_ids_are_validated(string owner, bool valid)
    {
        Assert.Equal(valid, OwnerScope.IsValid(owner));
        var scope = new OwnerScope();
        if (valid)
        {
            scope.Set(owner);
            Assert.Equal(owner, scope.OwnerId);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => scope.Set(owner));
            Assert.Null(scope.OwnerId);
        }
    }

    [Fact]
    public void Use_restores_the_previous_owner_and_require_checks_it()
    {
        var scope = new OwnerScope();
        Assert.Throws<OwnerScopeMissingException>(() => scope.Require("a"));
        scope.Set("a");
        using (scope.Use("b"))
        {
            Assert.Equal("b", scope.OwnerId);
            Assert.Throws<OwnerScopeMissingException>(() => scope.Require("a"));
        }

        Assert.Equal("a", scope.OwnerId);
        scope.Require("a");
    }

    [Fact]
    public void Allow_unscoped_lasts_until_disposed_and_nests()
    {
        var scope = new OwnerScope();
        Assert.False(scope.UnscopedAllowed);
        var outer = scope.AllowUnscoped();
        using (scope.AllowUnscoped())
        {
            Assert.True(scope.UnscopedAllowed);
        }

        Assert.True(scope.UnscopedAllowed);
        outer.Dispose();
        outer.Dispose();
        Assert.False(scope.UnscopedAllowed);
    }

    [Fact]
    public void The_owner_cannot_change_while_a_transaction_is_open()
    {
        var scope = new OwnerScope();
        var open = false;
        scope.AddTransactionProbe(() => open);
        scope.Set("a");
        open = true;
        scope.Set("a");
        Assert.Throws<OwnerScopeChangeException>(() => scope.Set("b"));
        Assert.Throws<OwnerScopeChangeException>(() => scope.Use("b"));
        open = false;
        scope.Set("b");
        Assert.Equal("b", scope.OwnerId);
    }

    [Theory]
    [InlineData("Host=db;Username=u;Password=p;Multiplexing=true")]
    [InlineData("Host=db;Username=u;Password=p;No Reset On Close=true")]
    public void Connection_strings_that_keep_session_state_are_refused(string cs) =>
        Assert.Throws<InvalidOperationException>(() => OwnerConnectionStrings.Prepare(cs, "x", 5));

    [Fact]
    public void Connection_strings_get_an_application_name_and_a_pool_size_unless_they_have_one()
    {
        var b = new Npgsql.NpgsqlConnectionStringBuilder(OwnerConnectionStrings.Prepare("Host=db;Username=u;Password=p", "contacts-backend", 20));
        Assert.Equal(("contacts-backend", 20), (b.ApplicationName, b.MaxPoolSize));
        var kept = new Npgsql.NpgsqlConnectionStringBuilder(
            OwnerConnectionStrings.Prepare("Host=db;Username=u;Password=p;Application Name=mine;Maximum Pool Size=7", "contacts-backend", 20));
        Assert.Equal(("mine", 7), (kept.ApplicationName, kept.MaxPoolSize));
        Assert.Equal("Host=unset", OwnerConnectionStrings.Prepare("Host=unset", "x", 1));
        Assert.Equal(string.Empty, OwnerConnectionStrings.Prepare(null, "x", 1));
    }
}

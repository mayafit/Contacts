using ContactsApi.Core.Entities;
using ContactsApi.WebApi.Controllers;
using ContactsApi.WebApi.Services;

namespace ContactsApi.Tests;

/// <summary>AD-45 rule 1: which labels may be created, renamed or deleted</summary>
public class LabelNamesTests
{
    private static readonly List<ContactGroup> Groups = new()
    {
        new() { ResourceName = "contactGroups/starred", Name = "Starred", GroupType = "SYSTEM_CONTACT_GROUP" },
        new() { ResourceName = "contactGroups/a1", Name = "Elta", GroupType = "USER_CONTACT_GROUP" },
        new() { ResourceName = "contactGroups/t1", Name = ContactsController.TrashLabelName, GroupType = "USER_CONTACT_GROUP" },
    };

    [Fact]
    public void A_name_is_short_unique_and_not_a_system_group()
    {
        Assert.Null(LabelNames.Check("Family trip", Groups, null));
        Assert.Equal("INVALID_LABEL", LabelNames.Check("", Groups, null)?.Code);
        Assert.Equal("INVALID_LABEL", LabelNames.Check(new string('a', 101), Groups, null)?.Code);
        Assert.Equal("INVALID_LABEL", LabelNames.Check("starred", Groups, null)?.Code);
        Assert.Equal("INVALID_LABEL", LabelNames.Check(ContactsController.TrashLabelName, Groups, null)?.Code);
        Assert.Equal("LABEL_EXISTS", LabelNames.Check("ELTA", Groups, null)?.Code);
        // A label keeps its own name (a change of case is a rename)
        Assert.Null(LabelNames.Check("ELTA", Groups, "contactGroups/a1"));
    }

    [Fact]
    public void Only_the_Owners_own_labels_change()
    {
        Assert.True(LabelNames.IsOwnLabel("contactGroups/a1", Groups));
        Assert.False(LabelNames.IsOwnLabel("contactGroups/starred", Groups));
        Assert.False(LabelNames.IsOwnLabel("contactGroups/t1", Groups));
        Assert.False(LabelNames.IsOwnLabel("contactGroups/zz", Groups));
    }
}

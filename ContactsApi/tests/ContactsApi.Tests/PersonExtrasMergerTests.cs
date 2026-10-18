using ContactsApi.Infrastructure.Google;
using Google.Apis.PeopleService.v1.Data;

namespace ContactsApi.Tests;

public class PersonExtrasMergerTests
{
    private static FieldMetadata Contact() => new() { Source = new Source { Type = "CONTACT" } };

    private static FieldMetadata Profile() => new() { Source = new Source { Type = "PROFILE" } };

    [Fact]
    public void Carries_app_data_the_kept_contact_does_not_have()
    {
        var kept = new Person { ResourceName = "people/a" };
        var other = new Person
        {
            ResourceName = "people/b",
            ClientData = new List<ClientData> { new() { Key = "crm", Value = "42", Metadata = Contact() } }
        };

        var result = PersonExtrasMerger.Merge(kept, new[] { other });

        Assert.Equal(new[] { "clientData" }, result.ChangedFields);
        var data = Assert.Single(result.Extras.ClientData);
        Assert.Equal("crm", data.Key);
        Assert.Equal("42", data.Value);
        Assert.Null(data.Metadata);
        Assert.Empty(result.NotCopied);
    }

    [Fact]
    public void Keeps_own_entries_first_and_skips_duplicates()
    {
        var kept = new Person
        {
            ImClients = new List<ImClient> { new() { Protocol = "skype", Username = "Itay", Metadata = Contact() } }
        };
        var other = new Person
        {
            ImClients = new List<ImClient>
            {
                new() { Protocol = "Skype", Username = "itay ", Metadata = Contact() },
                new() { Protocol = "jabber", Username = "itay@x", Metadata = Contact() }
            }
        };

        var result = PersonExtrasMerger.Merge(kept, new[] { other });

        Assert.Equal(new[] { "Itay", "itay@x" }, result.Extras.ImClients.Select(i => i.Username));
    }

    [Fact]
    public void Writes_nothing_when_nothing_is_new()
    {
        var kept = new Person { Locales = new List<Locale> { new() { Value = "he", Metadata = Contact() } } };
        var other = new Person { Locales = new List<Locale> { new() { Value = "HE", Metadata = Contact() } } };

        var result = PersonExtrasMerger.Merge(kept, new[] { other });

        Assert.Empty(result.ChangedFields);
        Assert.Null(result.Extras.Locales);
    }

    [Fact]
    public void Ignores_entries_from_linked_profiles()
    {
        var kept = new Person();
        var other = new Person
        {
            Occupations = new List<Occupation> { new() { Value = "Engineer", Metadata = Profile() } }
        };

        var result = PersonExtrasMerger.Merge(kept, new[] { other });

        Assert.Empty(result.ChangedFields);
    }

    [Fact]
    public void Takes_a_gender_only_when_the_kept_contact_has_none()
    {
        var male = new Gender { Value = "male", Metadata = Contact() };
        var female = new Gender { Value = "female", Metadata = Contact() };

        var filled = PersonExtrasMerger.Merge(new Person(), new[] { new Person { ResourceName = "people/b", Genders = new List<Gender> { male } } });
        Assert.Equal("male", Assert.Single(filled.Extras.Genders).Value);

        var conflict = PersonExtrasMerger.Merge(
            new Person { Genders = new List<Gender> { male } },
            new[] { new Person { ResourceName = "people/b", Genders = new List<Gender> { female } } });
        Assert.DoesNotContain("genders", conflict.ChangedFields);
        Assert.Equal(new[] { "gender" }, conflict.NotCopied["people/b"]);
    }

    [Fact]
    public void Reports_a_real_photo_and_new_skills_but_not_a_default_photo()
    {
        var withPhoto = new Person
        {
            ResourceName = "people/b",
            Photos = new List<Photo> { new() { Url = "u", Metadata = Contact() } },
            Skills = new List<Skill> { new() { Value = "C#", Metadata = Contact() } }
        };
        var defaultPhoto = new Person
        {
            ResourceName = "people/c",
            Photos = new List<Photo> { new() { Url = "u", Default__ = true, Metadata = Contact() } }
        };

        var result = PersonExtrasMerger.Merge(new Person(), new[] { withPhoto, defaultPhoto });

        Assert.Equal(new[] { "skills", "photo" }, result.NotCopied["people/b"]);
        Assert.False(result.NotCopied.ContainsKey("people/c"));
        Assert.DoesNotContain("skills", result.ChangedFields);
    }

    [Fact]
    public void Copies_events_without_formatted_fields()
    {
        var other = new Person
        {
            Events = new List<Event>
            {
                new() { Type = "anniversary", FormattedType = "Anniversary", Date = new Date { Year = 2010, Month = 6, Day = 1 }, Metadata = Contact() }
            }
        };

        var result = PersonExtrasMerger.Merge(new Person(), new[] { other });

        var ev = Assert.Single(result.Extras.Events);
        Assert.Equal("anniversary", ev.Type);
        Assert.Null(ev.FormattedType);
        Assert.Equal(2010, ev.Date.Year);
    }
}

using Google.Apis.PeopleService.v1.Data;

namespace ContactsApi.Infrastructure.Google;

/// <summary>
/// Merges the person fields the app does not show (events, IM, SIP, locations,
/// occupations, interests, keywords, external IDs, calendar links,
/// app data, languages, gender) from merged-away contacts into the kept one,
/// so a merge does not lose them. The kept contact's own entries always stay;
/// other contacts add the entries it does not have.
/// </summary>
public static class PersonExtrasMerger
{
    /// <summary>Person fields this merger reads (all writable except skills and photos)</summary>
    public const string Fields =
        "events,imClients,sipAddresses,locations,occupations,interests,skills,miscKeywords," +
        "externalIds,calendarUrls,clientData,locales,genders,photos";

    /// <summary>Outcome of a merge</summary>
    /// <param name="Extras">Person holding the merged lists for every changed field</param>
    /// <param name="ChangedFields">updatePersonFields names whose lists gained entries</param>
    /// <param name="NotCopied">Per merged-away contact, what could not be carried over</param>
    public sealed record Result(Person Extras, List<string> ChangedFields, Dictionary<string, List<string>> NotCopied);

    /// <summary>
    /// Merges the extra fields of <paramref name="others"/> into <paramref name="kept"/>.
    /// </summary>
    /// <param name="kept">Contact that is kept (read with <see cref="Fields"/>)</param>
    /// <param name="others">Contacts merged away (read with <see cref="Fields"/>)</param>
    /// <returns>Lists to write and what cannot be carried over</returns>
    public static Result Merge(Person kept, IReadOnlyList<Person> others)
    {
        var extras = new Person();
        var changed = new List<string>();
        var notCopied = new Dictionary<string, List<string>>();

        void Union<T>(string field, Func<Person, IList<T>?> get, Action<Person, IList<T>> set,
            Func<T, FieldMetadata?> meta, Func<T, string> key, Func<T, T> copy)
        {
            var result = Own(get(kept), meta).Select(copy).ToList();
            var seen = new HashSet<string>(result.Select(key));
            var before = result.Count;
            foreach (var item in others.SelectMany(o => Own(get(o), meta)))
            {
                var k = key(item);
                if (seen.Add(k))
                {
                    result.Add(copy(item));
                }
            }

            if (result.Count > before)
            {
                set(extras, result);
                changed.Add(field);
            }
        }

        Union("events", p => p.Events, (p, v) => p.Events = v, x => x.Metadata,
            x => K(x.Type, x.Date?.Year, x.Date?.Month, x.Date?.Day),
            x => new Event { Type = x.Type, Date = x.Date == null ? null : new Date { Year = x.Date.Year, Month = x.Date.Month, Day = x.Date.Day } });
        Union("imClients", p => p.ImClients, (p, v) => p.ImClients = v, x => x.Metadata,
            x => K(x.Protocol, x.Username),
            x => new ImClient { Protocol = x.Protocol, Type = x.Type, Username = x.Username });
        Union("sipAddresses", p => p.SipAddresses, (p, v) => p.SipAddresses = v, x => x.Metadata,
            x => K(x.Value),
            x => new SipAddress { Type = x.Type, Value = x.Value });
        Union("locations", p => p.Locations, (p, v) => p.Locations = v, x => x.Metadata,
            x => K(x.Value, x.BuildingId, x.Floor, x.DeskCode),
            x => new Location
            {
                Type = x.Type, Value = x.Value, BuildingId = x.BuildingId, Current = x.Current,
                DeskCode = x.DeskCode, Floor = x.Floor, FloorSection = x.FloorSection
            });
        Union("occupations", p => p.Occupations, (p, v) => p.Occupations = v, x => x.Metadata,
            x => K(x.Value), x => new Occupation { Value = x.Value });
        Union("interests", p => p.Interests, (p, v) => p.Interests = v, x => x.Metadata,
            x => K(x.Value), x => new Interest { Value = x.Value });
        Union("miscKeywords", p => p.MiscKeywords, (p, v) => p.MiscKeywords = v, x => x.Metadata,
            x => K(x.Type, x.Value), x => new MiscKeyword { Type = x.Type, Value = x.Value });
        Union("externalIds", p => p.ExternalIds, (p, v) => p.ExternalIds = v, x => x.Metadata,
            x => K(x.Type, x.Value), x => new ExternalId { Type = x.Type, Value = x.Value });
        Union("calendarUrls", p => p.CalendarUrls, (p, v) => p.CalendarUrls = v, x => x.Metadata,
            x => K(x.Url), x => new CalendarUrl { Type = x.Type, Url = x.Url });
        Union("clientData", p => p.ClientData, (p, v) => p.ClientData = v, x => x.Metadata,
            x => K(x.Key, x.Value), x => new ClientData { Key = x.Key, Value = x.Value });
        Union("locales", p => p.Locales, (p, v) => p.Locales = v, x => x.Metadata,
            x => K(x.Value), x => new Locale { Value = x.Value });

        // Gender: one per contact. Take another's only when the kept contact has none.
        var keptGender = Own(kept.Genders, g => g.Metadata).FirstOrDefault();
        foreach (var other in others)
        {
            var theirs = Own(other.Genders, g => g.Metadata).FirstOrDefault();
            if (theirs == null)
            {
                continue;
            }

            if (keptGender == null)
            {
                keptGender = theirs;
                extras.Genders = new List<Gender> { new() { Value = theirs.Value, AddressMeAs = theirs.AddressMeAs } };
                changed.Add("genders");
            }
            else if (K(keptGender.Value) != K(theirs.Value))
            {
                Note(notCopied, other, "gender");
            }
        }

        // Photos and skills cannot be written through updateContact; report the
        // ones the kept contact does not already have
        var keptSkills = new HashSet<string>(Own(kept.Skills, s => s.Metadata).Select(s => K(s.Value)));
        foreach (var other in others)
        {
            if (Own(other.Skills, s => s.Metadata).Any(s => !keptSkills.Contains(K(s.Value))))
            {
                Note(notCopied, other, "skills");
            }

            if (Own(other.Photos, p => p.Metadata).Any(p => p.Default__ != true))
            {
                Note(notCopied, other, "photo");
            }
        }

        return new Result(extras, changed, notCopied);
    }

    /// <summary>Entries that belong to the contact itself (not to a linked profile)</summary>
    private static IEnumerable<T> Own<T>(IList<T>? items, Func<T, FieldMetadata?> meta) =>
        items?.Where(i => meta(i)?.Source?.Type is null or "CONTACT") ?? Enumerable.Empty<T>();

    /// <summary>Case-insensitive identity from the given parts</summary>
    private static string K(params object?[] parts) =>
        string.Join("|", parts.Select(p => p?.ToString()?.Trim().ToLowerInvariant() ?? string.Empty));

    private static void Note(Dictionary<string, List<string>> notCopied, Person person, string what)
    {
        var key = person.ResourceName ?? string.Empty;
        if (!notCopied.TryGetValue(key, out var list))
        {
            notCopied[key] = list = new List<string>();
        }

        if (!list.Contains(what))
        {
            list.Add(what);
        }
    }
}

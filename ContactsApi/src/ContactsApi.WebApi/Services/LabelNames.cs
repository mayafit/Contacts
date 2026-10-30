/**
 * @fileoverview Which labels the Owner may create, rename or delete (AD-45 rule 1)
 * @module ContactsApi.WebApi.Services
 */
using ContactsApi.Core.Entities;
using ContactsApi.WebApi.Controllers;

namespace ContactsApi.WebApi.Services;

/// <summary>The guards of label management, pure</summary>
public static class LabelNames
{
    /// <summary>Longest label name</summary>
    public const int MaxLength = 100;

    /// <summary>
    /// Why a name cannot be given to a label: empty or too long, a system group's name,
    /// or another user label's (ignoring case)
    /// </summary>
    /// <param name="name">Trimmed name</param>
    /// <param name="groups">All the Owner's groups</param>
    /// <param name="renaming">The label being renamed (its own name is allowed), or null</param>
    /// <returns>The error code and message, or null when allowed</returns>
    public static (string Code, string Message)? Check(string name, IEnumerable<ContactGroup> groups, string? renaming)
    {
        if (name.Length is 0 or > MaxLength)
        {
            return ("INVALID_LABEL", $"A label's name is 1 to {MaxLength} characters");
        }

        if (string.Equals(name, ContactsController.TrashLabelName, StringComparison.OrdinalIgnoreCase))
        {
            return ("INVALID_LABEL", "That name is Echo's Trash");
        }

        foreach (var g in groups)
        {
            if (!string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase) || g.ResourceName == renaming)
            {
                continue;
            }

            return g.GroupType == "USER_CONTACT_GROUP"
                ? ("LABEL_EXISTS", $"A label named \"{g.Name}\" already exists")
                : ("INVALID_LABEL", $"\"{g.Name}\" is a system group");
        }

        return null;
    }

    /// <summary>A user label of the Owner's, not the Trash</summary>
    /// <param name="resourceName">The label</param>
    /// <param name="groups">All the Owner's groups</param>
    /// <returns>Whether it may be renamed or deleted</returns>
    public static bool IsOwnLabel(string resourceName, IEnumerable<ContactGroup> groups) =>
        groups.Any(g => g.ResourceName == resourceName && g.GroupType == "USER_CONTACT_GROUP"
            && g.Name != ContactsController.TrashLabelName);
}

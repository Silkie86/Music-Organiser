using System.Text.Json;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Sharing;

/// <summary>Someone you share projects with, and the plugins they have.</summary>
public sealed record Collaborator(string Name, IReadOnlyList<InstalledPlugin> Plugins, DateTime Updated)
{
    public PluginInventory Inventory => new(Name, Plugins);
}

/// <summary>Your collaborators' plugin lists, kept in the library.</summary>
public static class Collaborators
{
    const string Key = "collaborators";
    const string SelectedKey = "collaborator_selected";

    public static List<Collaborator> Load(LibraryDb db)
    {
        if (db.GetSetting(Key) is not { Length: > 0 } json) return [];
        try { return JsonSerializer.Deserialize<List<Collaborator>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static void Save(LibraryDb db, IEnumerable<Collaborator> people) =>
        db.SetSetting(Key, JsonSerializer.Serialize(people.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList()));

    /// <summary>Adds someone, or replaces their plugin list if they're already there.</summary>
    public static Collaborator Upsert(LibraryDb db, string name, PluginInventory plugins)
    {
        var people = Load(db);
        people.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var person = new Collaborator(name.Trim(), plugins.Plugins, DateTime.Now);
        people.Add(person);
        Save(db, people);
        return person;
    }

    public static void Remove(LibraryDb db, string name)
    {
        var people = Load(db);
        people.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Save(db, people);
    }

    public static string? Selected(LibraryDb db) => db.GetSetting(SelectedKey);
    public static void Select(LibraryDb db, string? name) => db.SetSetting(SelectedKey, name ?? "");
}

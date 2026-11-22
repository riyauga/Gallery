using System.Text.Json.Nodes;

namespace Gallery.Services;

public sealed partial class StoreService
{
    private string ApplyFilenameTags(string payload)
    {
        if (JsonNode.Parse(payload) is not JsonObject root || root["filename_tags"] is not JsonObject parsed)
            return payload;

        var known = AllTags();
        var output = new JsonObject();
        var selectedWorks = new List<TagInfo>();
        static string Fold(string value) => value.Trim().Replace(' ', '_').ToLowerInvariant();
        List<TagInfo> Matches(string category, string value) => known.Where(tag =>
            tag.Category == category && tag.SearchTerms.Any(term => Fold(term) == Fold(value))).ToList();

        foreach (var category in new[] { "work", "character", "artist", "kind", "rating", "custom" })
        {
            var values = new JsonArray();
            if (parsed[category] is JsonArray incoming)
            {
                foreach (var node in incoming)
                {
                    var value = node?.GetValue<string>()?.Trim() ?? "";
                    if (value.Length == 0) continue;
                    if (category == "rating")
                    {
                        var rating = ArchiveService.NormalizeRating(value);
                        if (rating.Length > 0) root["rating"] = rating;
                        continue;
                    }
                    var matches = Matches(category, value);
                    if (category == "character" && matches.Count > 1 && selectedWorks.Count > 0)
                    {
                        var keys = selectedWorks.SelectMany(work => new[] { work.IdentityKey, work.EntityKey, work.GameKey })
                            .Where(key => key.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        matches = matches.Where(tag => keys.Contains(tag.GameKey)).ToList();
                    }
                    if (matches.Count > 1) continue;
                    if (category == "work" && matches.Count == 1) selectedWorks.Add(matches[0]);
                    values.Add(matches.Count == 1 ? matches[0].Name : value);
                }
            }
            output[category] = values;
        }
        root["review_tags"] = output;
        return root.ToJsonString();
    }
}

using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>Displays and toggles the trainer's Kanto and Johto badges.</summary>
public static class TrainerBadgesEditor
{
    public static async Task ShowAsync(
        Grid host,
        ISaveEngineSession session,
        Func<int, bool, Task<bool>> setBadgeAsync)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(setBadgeAsync);

        while (true)
        {
            var info = session.GetTrainerBadges();

            if (!info.Supported)
            {
                await EditorMenu.ShowAsync(
                    host,
                    "Trainer badges",
                    "This save format does not expose trainer badge data.",
                    "OK");
                return;
            }

            var labels = info.Badges.ToDictionary(
                badge => badge.Id,
                badge => $"{(badge.Earned ? "✓" : "□")} {badge.Name} Badge");

            var options = info.Badges.Select(badge =>
                new PadOption(
                    labels[badge.Id],
                    Detail: $"{badge.Region} · {(badge.Earned ? "Earned" : "Not earned")}"))
                .ToArray();

            var choice = await PadMenu.ShowAsync(
                host,
                $"Trainer badges · {info.EarnedCount}/16",
                "Select a badge to toggle its earned state.",
                options);

            if (choice is null)
                return;

            var selected = info.Badges.FirstOrDefault(
                badge => labels[badge.Id] == choice);

            if (selected is null)
                return;

            if (!await setBadgeAsync(selected.Id, !selected.Earned))
                return;
        }
    }
}
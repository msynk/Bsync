using Bsync.Client;
using Bsync.Samples.Tasks;
using Bsync.Samples.Tasks.Console;

// Usage: tasks [--server URL] [--data DIR] [--user NAME] [--team NAME]
//        (add TITLE | done ID | rename ID TITLE | intents | list | sync)
// Without a reachable server, "add" still saves locally; a later "sync" uploads it.
var options = args.Chunk(2).Where(pair => pair.Length == 2 && pair[0].StartsWith("--", StringComparison.Ordinal)).ToDictionary(pair => pair[0], pair => pair[1]);
var command = args.SkipWhile((arg, i) => arg.StartsWith("--", StringComparison.Ordinal) || (i > 0 && args[i - 1].StartsWith("--", StringComparison.Ordinal))).ToArray();

await using var client = TasksClient.Create(
    new Uri(options.GetValueOrDefault("--server", "http://localhost:5000/")),
    options.GetValueOrDefault("--data", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bsync.Samples.Tasks")),
    options.GetValueOrDefault("--user", "alice"),
    options.GetValueOrDefault("--team", "team-1"));

switch (command.FirstOrDefault())
{
    case "add" when command.Length > 1:
        var saved = await client.Tasks.SaveAsync(new TaskDocument { Title = string.Join(' ', command.Skip(1)) });
        Console.WriteLine($"Saved {saved.Id} ({saved.Confirmation}).");
        break;

    // Intents (task F3): requests the server executes once, against the revision this device saw.
    case "done" when command.Length > 1:
        var completion = await client.CompleteAsync(command[1]);
        Console.WriteLine($"Asked to complete {command[1]} (intent {completion.Id}); it runs on the server after the next sync.");
        break;

    case "rename" when command.Length > 2:
        var rename = await client.RenameAsync(command[1], string.Join(' ', command.Skip(2)));
        Console.WriteLine($"Asked to rename {command[1]} (intent {rename.Id}); it runs on the server after the next sync.");
        break;

    case "intents":
        foreach (var intent in (await client.Intents.QueryAsync()).OrderBy(i => i.CreatedAt))
        {
            var sync = await client.Intents.GetItemStatusAsync(intent.Id);
            Console.WriteLine($"{intent.CreatedAt:u} {intent.Kind} {intent.TaskId}: {intent.State}{(intent.Code is { } code ? " (" + code + ")" : string.Empty)}, sync {sync?.State}");
        }

        break;

    case "sync":
        try
        {
            var result = await client.SyncNowAsync();
            Console.WriteLine($"Pulled {result.Pulled}, pushed {result.Pushed}, rejected {result.Rejected}, complete: {result.IsComplete}.");
        }
        catch (Exception error) when (error is Bsync.SyncTransportException or HttpRequestException)
        {
            Console.WriteLine($"Offline: {error.Message} Local work is kept.");
        }

        break;

    default:
        foreach (var task in await client.Tasks.QueryAsync())
        {
            var status = await client.Tasks.GetItemStatusAsync(task.Id);
            Console.WriteLine($"{(task.Done ? "[x]" : "[ ]")} {task.Title} ({task.Slug}) {status?.State}{(status?.Detail is { } detail ? ": " + detail : string.Empty)}");
        }

        break;
}

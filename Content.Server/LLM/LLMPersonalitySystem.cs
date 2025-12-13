using Content.Server.NPC.HTN;
using Content.Shared.Nutrition.Components;
using Content.Server.NPC;
using Content.Server.NPC.Systems;
using System.Net.Http;
using System.Threading.Tasks;
using Robust.Shared.Asynchronous;

namespace Content.Server.LLM;

public sealed class LLMPersonalitySystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly NPCSystem _npc = default!;
    [Dependency] private readonly HTNSystem _htn = default!;
    [Dependency] private readonly ITaskManager _taskManager = default!;

    // 1. Define the timer variables
    private float _accumulatedTime = 0f;
    private const float UpdateInterval = 5.0f; // Run every 5 seconds
    private static readonly HttpClient HttpClient = new HttpClient();

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // 2. Add the time passed since the last frame (delta time) to our counter
        _accumulatedTime += frameTime;

        // 3. Check: Has 5 seconds passed?
        if (_accumulatedTime < UpdateInterval)
        {
            // If not, stop here. Do nothing this tick.
            return;
        }

        // 4. Reset the timer (subtract the interval to keep rhythm)
        _accumulatedTime -= UpdateInterval;

        // --- EXPENSIVE LOGIC STARTS HERE ---
        // This block now only runs once every 5 seconds
        foreach (var (hunger, htn, personality) in EntityQuery<HungerComponent, HTNComponent, LLMPersonalityComponent>())
        {
            var uid = hunger.Owner;

            // 1. Gather Sensory Data
            var status = $"I am feeling {hunger.CurrentThreshold}.";

            var visibleEntities = new List<string>();
            foreach (var entity in _lookup.GetEntitiesInRange(uid, 10f))
            {
                if (entity == uid) continue;
                var meta = MetaData(entity);
                visibleEntities.Add($"{meta.EntityName} (ID: {entity})");
            }

            var visionText = visibleEntities.Count > 0
                ? "I can see: " + string.Join(", ", visibleEntities)
                : "I can see nothing.";

            // 2. Send to LLM endpoint
            // We fire and forget this async task to avoid blocking the game loop
            // In a real production system, you'd want a proper job queue.
            ProcessLLMDecision(uid, status, visionText);
        }
    }

    private async void ProcessLLMDecision(EntityUid uid, string status, string vision)
    {
        try
        {
            var prompt = $@"
You are an NPC in Space Station 14.
Status: {status}
Vision: {vision}
Your goal is to survive and satisfy your needs.
Available Commands:
- NOOP
- MOVE <TargetID> (e.g., MOVE 123)

Respond with ONLY the command.
";

            // Mocking the request for now to avoid actual external dependency errors
            // until user confirms they have an endpoint running.
            // For now, let's just pick a random visible entity to move to if we are hungry.

            // In the future:
            // var content = new StringContent(JsonSerializer.Serialize(new { prompt = prompt }));
            // var response = await HttpClient.PostAsync("http://localhost:5000/v1/chat/completions", content);

            // Simulate processing delay
            await Task.Delay(100);

            // Mock logic: If we see something, move to the first thing we see.
            // This proves the pipeline works.
            string responseCommand = "NOOP";

            // Simple heuristic to verify the system works:
            // If the prompt contains "MOVE_TEST", we assume the LLM said it.
            // Parsing the prompt itself is silly but efficient for a 'mock'.
            // Actually, let's parse the 'Vision' string we passed in to find a valid ID.
            if (vision.Contains("(ID: "))
            {
                // extract ID
                var parts = vision.Split("(ID: ");
                if (parts.Length > 1)
                {
                    var idPart = parts[1].Split(")")[0].Trim();
                     if (int.TryParse(idPart, out int targetId))
                     {
                         responseCommand = $"MOVE {targetId}";
                     }
                }
            }

            // 3. Translate LLM text into Game Actions
            // Safely schedule back to main thread
            _taskManager.RunOnMainThread(() =>
            {
                if (Exists(uid)) // Check if entity still exists
                {
                    ParseAndAct(uid, responseCommand);
                }
            });
        }
        catch (Exception e)
        {
            Logger.Error($"LLM Error: {e.Message}");
        }
    }

    private void ParseAndAct(EntityUid uid, string command)
    {
        var parts = command.Split(' ');
        if (parts[0] == "MOVE" && parts.Length > 1)
        {
             if (int.TryParse(parts[1], out int targetIdVal))
             {
                 var target = new EntityUid(targetIdVal);
                 if (Exists(target))
                 {
                     var targetCoords = Transform(target).Coordinates;
                     _npc.SetBlackboard(uid, NPCBlackboard.MovementTarget, targetCoords);

                     // Force replan to pick up the new blackboard value immediately
                     if (TryComp<HTNComponent>(uid, out var htn))
                     {
                         _htn.Replan(htn);
                     }
                 }
             }
        }
    }

}

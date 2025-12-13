using Content.Server.NPC.HTN;
using Content.Shared.Nutrition.Components;
using Content.Server.NPC;
using Content.Server.NPC.Systems;
using System.Net.Http;
using System.Threading.Tasks;
using Robust.Shared.Asynchronous;
using OpenAI.Chat;

namespace Content.Server.LLM;

public sealed class LLMPersonalitySystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly NPCSystem _npc = default!;
    [Dependency] private readonly HTNSystem _htn = default!;
    [Dependency] private readonly ITaskManager _taskManager = default!;
    [Dependency] private readonly ILLMService _llmService = default!;

    // 1. Define the timer variables
    private float _accumulatedTime = 0f;
    private const float UpdateInterval = 5.0f; // Run every 5 seconds

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

            // 2. Clone history for async use (Snapshot)
            var historySnapshot = new List<LLMPersonalityComponent.PersonalityChatMessage>(personality.History);

            // 3. Send to LLM endpoint
            ProcessLLMDecision(uid, status, visionText, historySnapshot);
        }
    }

    private async void ProcessLLMDecision(EntityUid uid, string status, string vision, List<LLMPersonalityComponent.PersonalityChatMessage> history)
    {
        try
        {
            var systemPrompt = @"
You are an NPC in Space Station 14.
Your goal is to survive and satisfy your needs.
Available Commands:
- NOOP
- MOVE <TargetID> (e.g., MOVE 123)
Respond with ONLY the command.
";
            var userPrompt = $@"
Status: {status}
Vision: {vision}
";

            // Build full message chain
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt)
            };

            foreach (var msg in history)
            {
                if (msg.Role == "user") messages.Add(new UserChatMessage(msg.Content));
                else if (msg.Role == "assistant") messages.Add(new AssistantChatMessage(msg.Content));
            }

            messages.Add(new UserChatMessage(userPrompt));

            // Call Service
            var responseText = await _llmService.GenerateResponseAsync(messages);

            // 3. Translate LLM text into Game Actions
            // Safely schedule back to main thread
            _taskManager.RunOnMainThread(() =>
            {
                if (Exists(uid)) // Check if entity still exists
                {
                    // Update History
                    if (TryComp<LLMPersonalityComponent>(uid, out var personality))
                    {
                        personality.History.Add(new LLMPersonalityComponent.PersonalityChatMessage("user", userPrompt));
                        personality.History.Add(new LLMPersonalityComponent.PersonalityChatMessage("assistant", responseText));

                        // Prune if > 10 messages (5 turns)
                        if (personality.History.Count > 10)
                        {
                            personality.History.RemoveRange(0, personality.History.Count - 10);
                        }
                    }

                    ParseAndAct(uid, responseText);
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
        if (string.IsNullOrWhiteSpace(command)) return;

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

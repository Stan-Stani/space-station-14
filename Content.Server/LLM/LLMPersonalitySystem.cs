using Content.Server.NPC.HTN;
using Content.Shared.Nutrition.Components;
using System.Linq;
using Content.Server.NPC;
using Content.Server.NPC.Systems;
using System.Net.Http;
using System.Threading.Tasks;
using Robust.Shared.Asynchronous;
using OpenAI.Chat;
using Content.Server.Chat.Systems;
using Content.Shared.Chat;
using Robust.Shared.Player;
using System.IO;

namespace Content.Server.LLM;

public sealed class LLMPersonalitySystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly NPCSystem _npc = default!;
    [Dependency] private readonly HTNSystem _htn = default!;
    [Dependency] private readonly ITaskManager _taskManager = default!;
    [Dependency] private readonly ILLMService _llmService = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<EntitySpokeEvent>(OnEntitySpoke);
    }

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

            var entities = _lookup.GetEntitiesInRange(uid, 10f);

            var groups = entities
                .Where(e => e != uid)
                .GroupBy(e => MetaData(e).EntityName);

            foreach (var group in groups)
            {
                 var count = group.Count();
                 var name = group.Key;
                 if (count == 1)
                 {
                     visibleEntities.Add($"{name} (ID: {group.First()})");
                 }
                 else
                 {
                     var ids = string.Join(", ", group.Take(3).Select(e => e.ToString()));
                     if (count > 3) ids += ", ...";
                     visibleEntities.Add($"{count}x {name} (IDs: {ids})");
                 }
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
- SPEAK <Message> (e.g., SPEAK ""Hello there!"")
Respond with ONLY the command.
";
            var userPrompt = $@"
Status: {status}
Vision: {vision}
";

            // Build full message chain
            var messages = new List<OpenAI.Chat.ChatMessage>
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
                    var cleanResponse = responseText.Trim();
                    if (string.Equals(cleanResponse, "NOOP", StringComparison.OrdinalIgnoreCase))
                        return;

                    // Update History
                    if (TryComp<LLMPersonalityComponent>(uid, out var personality))
                    {
                        // Note: We do NOT add the transient 'userPrompt' (Status/Vision) to the permanent history.
                        // We only add what the assistant actually did/said.
                        personality.History.Add(new LLMPersonalityComponent.PersonalityChatMessage("assistant", cleanResponse));

                        LogConversation(uid, "Context", userPrompt);
                        LogConversation(uid, "Assistant", cleanResponse);

                        // Prune if > 10 messages (5 turns)
                        if (personality.History.Count > 10)
                        {
                            personality.History.RemoveRange(0, personality.History.Count - 10);
                        }
                    }

                    ParseAndAct(uid, cleanResponse);
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
        else if (parts[0] == "SPEAK" && parts.Length > 1)
        {
            // Reconstruct the message (it might have spaces)
            var message = string.Join(" ", parts.Skip(1)).Trim('"');
            _chat.TrySendInGameICMessage(uid, message, InGameICChatType.Speak, false);
        }
    }

    private void OnEntitySpoke(EntitySpokeEvent args)
    {
        // Don't listen to ourselves
        if (HasComp<LLMPersonalityComponent>(args.Source)) return;

        // Listen to anyone speaking nearby
        var query = EntityQueryEnumerator<LLMPersonalityComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var personality, out var xform))
        {
            if (_transform.InRange(xform.Coordinates, Transform(args.Source).Coordinates, 10f)) // Hearing range
            {
                 var speakerName = Name(args.Source);
                 var message = args.Message;

                 // Add to history
                 personality.History.Add(new LLMPersonalityComponent.PersonalityChatMessage("user", $"[Speaker: {speakerName}] {message}"));
                 LogConversation(uid, "User (Heard)", $"[Speaker: {speakerName}] {message}");

                 // Prune if needed
                 if (personality.History.Count > 10)
                 {
                     personality.History.RemoveRange(0, personality.History.Count - 10);
                 }

                 // Trigger immediate thought? Or wait for next update?
                 // For now, let the periodic update handle it to avoid spamming the LLM on every message.
                 // But checking the update loop, it processes history... yes.
            }
                }
    }

    private void LogConversation(EntityUid uid, string role, string content)
    {
        try
        {
            var logPath = "llm_conversation.log";
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{uid}] [{role}]: {content}\n");
        }
        catch (Exception e)
        {
            // Fallback to internal logger if file write fails, to ensure we don't crash
             Logger.ErrorS("llm", $"Failed to log conversation object to file: {e.Message}");
        }
    }
}


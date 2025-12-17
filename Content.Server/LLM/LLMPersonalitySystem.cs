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
using Content.Shared.Interaction;
using Content.Shared.Item;
using Content.Shared.Mobs.Components;
using Content.Shared.Doors.Components;
using Content.Shared.UserInterface;

using Content.Shared.Body.Part;

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
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<EntitySpokeEvent>(OnEntitySpoke);
    }

    // 1. Define the timer variables
    // 1. Define the timer constants
    private const float BackgroundUpdateInterval = 60.0f; // Run every 60 seconds if no speech
    private const float SpeechDebounceTime = 5.0f; // Wait 5 seconds after speech before triggering

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<LLMPersonalityComponent, HungerComponent, HTNComponent>();
        while (query.MoveNext(out var uid, out var personality, out var hunger, out var htn))
        {
            // 2. Update Timers
            personality.TimeSinceLastUpdate += frameTime;

            bool triggerUpdate = false;

            // Check Debounce Timer
            if (personality.SpeechDebounceTimer > 0f)
            {
                personality.SpeechDebounceTimer -= frameTime;
                if (personality.SpeechDebounceTimer <= 0f)
                {
                    // Timer expired! Trigger update.
                    triggerUpdate = true;
                    // Ensure it stays at 0
                    personality.SpeechDebounceTimer = 0f;
                }
            }
            // Check Background Timer (only if not currently debouncing)
            else if (personality.TimeSinceLastUpdate >= BackgroundUpdateInterval)
            {
                triggerUpdate = true;
            }

            // 3. Trigger Logic
            if (triggerUpdate)
            {
                personality.TimeSinceLastUpdate = 0f; // Reset background timer

                // --- EXPENSIVE LOGIC STARTS HERE ---
                // 1. Gather Sensory Data
                var status = $"I am feeling {hunger.CurrentThreshold}.";

                var visibleEntities = new List<string>();

                // Get all entities in range
                var entities = _lookup.GetEntitiesInRange(uid, 10f);

                // Filter and Sort Entities
                var salientEntities = entities
                    .Where(e => e != uid) // Don't see self
                    .Where(e => IsSalient(e)) // Must be interesting
                    .Where(e => _interaction.InRangeUnobstructed(uid, e, 10f)) // Must be visible (LOS)
                    .OrderBy(e => _transform.GetWorldPosition(e).LengthSquared()) // Closest first (approx)
                    .Take(20); // Limit to 20

                var groups = salientEntities
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

                        // Prune if > 3 messages
                        if (personality.History.Count > 3)
                        {
                            personality.History.RemoveRange(0, personality.History.Count - 3);
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

                // Reset debounce timer to wait for silence
                // This will delay the LLM response until 5 seconds AFTER the last spoke message.
                personality.SpeechDebounceTimer = SpeechDebounceTime;
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

    private bool IsSalient(EntityUid uid)
    {
        // Check for attached body parts
        if (TryComp<BodyPartComponent>(uid, out var bodyPart))
        {
            // If it has a parent Body, it's attached. Ignore it.
            if (bodyPart.Body != null)
                return false;
        }

        // Filter what we consider "interesting" to look at
        return HasComp<ItemComponent>(uid) ||
               HasComp<MobStateComponent>(uid) ||
               HasComp<DoorComponent>(uid) ||
               HasComp<ActivatableUIComponent>(uid);
    }
}


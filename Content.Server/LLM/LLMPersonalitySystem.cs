using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks.Operators;
using Content.Shared.Nutrition.Components;
using System;
using System.Collections.Generic;
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
using Content.Shared.Body.Components;
using Content.Shared.Body.Organ;
using Content.Shared.Humanoid;
using Content.Shared.Mobs;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Hands.Components;
using Content.Server.Mapping;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Containers;
using System.Text.RegularExpressions;

namespace Content.Server.LLM;

public sealed partial class LLMPersonalitySystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly NPCSystem _npc = default!;
    [Dependency] private readonly HTNSystem _htn = default!;
    [Dependency] private readonly ITaskManager _taskManager = default!;
    [Dependency] private readonly ILLMService _llmService = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly ILogManager _logManager = default!;
    private ISawmill _sawmill = default!;

    private static readonly Regex CommandExtractRegex = new(
        @"\[\~[A-Za-z0-9]+\~\][^\r\n]*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WhitespaceCollapseRegex = new(
        @"[ \t]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ThinkBlockRegex = new(
        @"<think>[\s\S]*?</think>",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public override void Initialize()
    {
        base.Initialize();
        _sawmill = _logManager.GetSawmill("llm");
        SubscribeLocalEvent<EntitySpokeEvent>(OnEntitySpoke);
    }

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
                if (personality.PendingLLMRequest)
                    continue;

                personality.TimeSinceLastUpdate = 0f; // Reset background timer

                // --- EXPENSIVE LOGIC STARTS HERE ---
                // 1. Gather Sensory Data
                var hungerStatus = $"I am feeling {hunger.CurrentThreshold}.";

                // Health state
                string healthStatus = "healthy";
                if (TryComp<MobStateComponent>(uid, out var mobState))
                {
                    healthStatus = mobState.CurrentState switch
                    {
                        MobState.Alive => "healthy",
                        MobState.Critical => "critical",
                        MobState.Dead => "dead",
                        _ => "unknown"
                    };
                }
                var status = $"{hungerStatus} I am {healthStatus}.";

                var visibleEntities = new List<string>();

                // Get all entities in range
                var entities = _lookup.GetEntitiesInRange(uid, 10f);

                // Filter and Sort Entities
                var salientEntities = entities
                    .Where(e => e != uid) // Don't see self
                    .Where(e => IsSalient(e)) // Must be interesting
                    .Where(IsEntityInWorld)
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

                // Inventory context
                var inventoryText = GetInventoryText(uid);

                // 2. Clone history for async use (Snapshot)
                var historySnapshot = new List<LLMPersonalityComponent.PersonalityChatMessage>(personality.History);

                // 3. Send to LLM endpoint
                personality.PendingLLMRequest = true;
                GetAndProcessLLMDecision(uid, personality, status, visionText, inventoryText, historySnapshot);
            }
        }
    }

    private string GetInventoryText(EntityUid uid)
    {
        if (!TryComp<HandsComponent>(uid, out var handsComp))
            return "Inventory: no hands.";

        var items = new List<string>();
        foreach (var handName in handsComp.SortedHands)
        {
            var heldItem = _hands.GetHeldItem((uid, handsComp), handName);
            if (heldItem != null)
                items.Add($"{Name(heldItem.Value)} ({handName} hand)");
            else
                items.Add($"nothing ({handName} hand)");
        }

        if (items.Count == 0)
            return "Inventory: no hands.";

        return "I am holding: " + string.Join(", ", items);
    }

    public bool IsEntityAlive(EntityUid entityUid)
    {
        if (EntityManager.TryGetComponent<MobStateComponent>(entityUid, out var mobState))
        {
            return mobState.CurrentState == MobState.Alive;
        }

        return false;
    }

    /// <summary>
    /// Not in inventory etc
    /// </summary>
    public bool IsEntityInWorld(EntityUid entityUid)
    {
        if (_container.IsEntityInContainer(entityUid))
            return false;

        return true;
    }


    private async void GetAndProcessLLMDecision(EntityUid uid, LLMPersonalityComponent personality, string status, string vision, string inventory, List<LLMPersonalityComponent.PersonalityChatMessage> history)
    {
        try
        {
            var personalityPrompt = !string.IsNullOrWhiteSpace(personality.Personality)
                ? $"\nPersonality: {personality.Personality}"
                : "";

            var systemPrompt = $@"/no_think
You are an NPC in Space Station 14.{personalityPrompt}
IMPORTANT: If someone spoke to you, ALWAYS reply with [~SPEAK~] first.
Commands:
[~SPEAK~] <Message> — say something. Use this to reply when spoken to.
[~INTERACT~] <TargetID> — move to entity and interact (pick up, open door, use item on target).
[~MOVE~] <TargetID> — walk to entity without interacting.
[~USE~] — activate held item (eat food, turn on flashlight).
[~DROP~] — drop held item.
[~NOOP~] — do nothing.
You can chain commands. Respond with ONLY commands.";

            var userPrompt = $@"
Status: {status}
Vision: {vision}
Inventory: {inventory}
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
            ProcessLLMDecision(uid, responseText, userPrompt);
        }
        catch (Exception e)
        {
            Logger.Error($"LLM Error: {e.Message}");
        }
        finally
        {
            _taskManager.RunOnMainThread(() =>
            {
                if (TryComp<LLMPersonalityComponent>(uid, out var comp))
                    comp.PendingLLMRequest = false;
            });
        }
    }

    public void ProcessLLMDecision(EntityUid uid, string? responseText, string userPrompt)
    {
        _taskManager.RunOnMainThread(() =>
          {
              if (!Exists(uid)) // Check if entity still exists
                  return;

              var rawResponse = (responseText ?? string.Empty).Trim();

              // Strip Qwen 3 <think>...</think> blocks if present
              var cleanResponse = ThinkBlockRegex.Replace(rawResponse, "").Trim();

              var commands = CommandExtractRegex.Matches(cleanResponse)
                  .Select(m =>
                  {
                      var cmd = m.Value.Trim();

                      // Collapse extra whitespace but keep the command token intact
                      cmd = WhitespaceCollapseRegex.Replace(cmd, " ").Trim();

                      // Ensure it's a single line
                      cmd = cmd.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries)
                          .FirstOrDefault()?.Trim() ?? string.Empty;

                      return cmd;
                  })
                  .Where(cmd => !string.IsNullOrWhiteSpace(cmd))
                  .ToList();

              if (commands.Count == 0)
                  return;

              // Remove NOOP commands (but still allow other commands in the same response)
              commands.RemoveAll(cmd => string.Equals(cmd, "[~NOOP~]", StringComparison.OrdinalIgnoreCase));

              if (commands.Count == 0)
                  return;

              // Update History (store the whole batch as a single assistant turn)
              if (TryComp<LLMPersonalityComponent>(uid, out var personality))
              {
                  var historyEntry = string.Join("\n", commands);
                  personality.History.Add(new LLMPersonalityComponent.PersonalityChatMessage("assistant", historyEntry));

                  LogConversation(uid, "Context", userPrompt);
                  LogConversation(uid, "Assistant", historyEntry);

                  // Prune if > 3 messages
                  if (personality.History.Count > 3)
                  {
                      personality.History.RemoveRange(0, personality.History.Count - 3);
                  }
              }

              // Execute all commands in order
              foreach (var cmd in commands)
              {
                  ParseAndAct(uid, cmd);
              }
          });
    }


    private void ParseAndAct(EntityUid uid, string commandAndArgs)
    {
        if (string.IsNullOrWhiteSpace(commandAndArgs)) return;

        var parts = commandAndArgs.Split(' ');
        if (CommandSyntaxRegex().IsMatch(parts[0]) == false)
        {
            _sawmill.Warning($"Invalid command format: {commandAndArgs}");
            return;
        }

        string command = parts[0].ToUpperInvariant();

        switch (command)
        {
            case "[~DROP~]":
                _npc.SetBlackboard(uid, "ShouldDrop", true);
                if (TryComp<HTNComponent>(uid, out var dropHtn))
                    _htn.Replan(dropHtn);
                break;

            case "[~NOOP~]":
                break;

            case "[~MOVE~]":
                if (parts.Length < 2) break;
                if (int.TryParse(parts[1], out int moveTargetIdVal))
                {
                    var target = new EntityUid(moveTargetIdVal);
                    if (Exists(target))
                    {
                        if (TryComp<HTNComponent>(uid, out var moveHtn) && !CanSafelySetMovementTarget(moveHtn))
                            break;

                        var targetCoords = _transform.GetMoverCoordinates(target);
                        _npc.SetBlackboard(uid, NPCBlackboard.MovementTarget, targetCoords);
                    }
                }
                break;

            case "[~INTERACT~]":
                if (parts.Length < 2) break;
                if (int.TryParse(parts[1], out int interactId))
                {
                    var target = new EntityUid(interactId);
                    if (Exists(target))
                    {
                        var targetCoords = _transform.GetMoverCoordinates(target);
                        _npc.SetBlackboard(uid, "InteractTarget", target);
                        _npc.SetBlackboard(uid, NPCBlackboard.MovementTarget, targetCoords);
                        if (TryComp<HTNComponent>(uid, out var htn))
                            _htn.Replan(htn);
                    }
                }
                break;

            case "[~USE~]":
                if (_hands.TryGetActiveItem(uid, out var heldEntity))
                {
                    _interaction.UseInHandInteraction(uid, heldEntity.Value);
                }
                break;

            case "[~SPEAK~]":
                if (parts.Length < 2) break;
                var message = string.Join(" ", parts.Skip(1)).Trim('"');
                _chat.TrySendInGameICMessage(uid, message, InGameICChatType.Speak, false);
                break;
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
                var speakerUid = args.Source;
                var speechMessage = args.Message;

                // Add to history
                personality.History.Add(new LLMPersonalityComponent.PersonalityChatMessage("user", $"[Speaker: {speakerName}[{speakerUid}]] {speechMessage}"));
                LogConversation(uid, "User (Heard)", $"[Speaker: {speakerName}[{speakerUid}]] {speechMessage}");

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

        // Check for attached organs
        if (TryComp<OrganComponent>(uid, out var organ))
        {
            if (organ.Body != null)
                return false;
        }

        // Filter what we consider "interesting" to look at
        return HasComp<ItemComponent>(uid) ||
               HasComp<MobStateComponent>(uid) ||
               HasComp<DoorComponent>(uid) ||
               HasComp<ActivatableUIComponent>(uid) ||
               HasComp<BodyComponent>(uid) ||
               HasComp<HumanoidAppearanceComponent>(uid);
    }

    private static bool CanSafelySetMovementTarget(HTNComponent htn)
    {
        // The crash scenario happens when a new plan is built using MovementTarget from a blackboard clone,
        // then the *current* MoveToOperator shuts down and removes MovementTarget before the new plan starts.
        // Avoid triggering that by not touching MovementTarget while such an operator is active.
        if (htn.Plan?.CurrentOperator is not MoveToOperator move)
            return true;

        return !string.Equals(move.TargetKey, NPCBlackboard.MovementTarget, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^\[\~[A-Za-z0-9]+\~\]$")]
    private static partial Regex CommandSyntaxRegex();
}

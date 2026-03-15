using Robust.Shared.GameObjects;
using System.Collections.Generic;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Server.LLM;

[RegisterComponent]
public sealed partial class LLMPersonalityComponent : Component
{
    [DataField("history")]
    public List<PersonalityChatMessage> History = new();

    /// <summary>
    /// Timer for buffering speech inputs.
    /// When > 0, we are waiting for silence.
    /// When it hits 0, we trigger an update.
    /// </summary>
    [DataField("speechDebounceTimer")]
    public float SpeechDebounceTimer = 0f;

    /// <summary>
    /// Time since the last LLM update was triggered.
    /// Used for the background "I'm still here" update (e.g. every 60s).
    /// </summary>
    [DataField("timeSinceLastUpdate")]
    public float TimeSinceLastUpdate = 0f;

    /// <summary>
    /// Guards against concurrent LLM requests for this entity.
    /// </summary>
    public bool PendingLLMRequest;

    /// <summary>
    /// Optional personality description interpolated into the system prompt.
    /// </summary>
    [DataField("personality")]
    public string Personality = string.Empty;

    [DataDefinition]
    public partial struct PersonalityChatMessage
    {
        [DataField("role")]
        public string Role;

        [DataField("content")]
        public string Content;

        public PersonalityChatMessage(string role, string content)
        {
            Role = role;
            Content = content;
        }
    }
}

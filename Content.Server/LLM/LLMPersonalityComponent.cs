using Robust.Shared.GameObjects;
using System.Collections.Generic;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Server.LLM;

[RegisterComponent]
public sealed partial class LLMPersonalityComponent : Component
{
    [DataField("history")]
    public List<PersonalityChatMessage> History = new();

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

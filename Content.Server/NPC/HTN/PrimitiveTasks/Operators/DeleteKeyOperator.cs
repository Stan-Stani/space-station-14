namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators;

public sealed partial class DeleteKeyOperator : HTNOperator
{
    [DataField("key", required: true)]
    public string Key = string.Empty;

    public override void Startup(NPCBlackboard blackboard)
    {
        // We remove it immediately on startup because once this
        // operator is reached in the plan, its only job is to wipe the key.
        blackboard.Remove<object>(Key);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        return HTNOperatorStatus.Finished;
    }
}

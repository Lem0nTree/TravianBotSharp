namespace MainCore.Models;

public sealed record ConstructionStep(int Slot, BuildingEnums Type, int Level)
{
    public NormalBuildPlan ToPlan() => new() { Location = Slot, Type = Type, Level = Level };
}

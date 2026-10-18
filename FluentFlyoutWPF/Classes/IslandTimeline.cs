namespace FluentFlyoutWPF.Classes;

/// <summary>A timeline read once from the player; painting only extrapolates this value.</summary>
public readonly record struct IslandTimeline(double Minimum, double Maximum, double Position,
    DateTimeOffset UpdatedAt, bool Playing, bool Seekable)
{
    public bool Valid => double.IsFinite(Minimum) && double.IsFinite(Maximum)
        && double.IsFinite(Position) && Maximum > Minimum && Minimum >= 0;

    public double PositionAt(DateTimeOffset now) => !Valid ? 0 : Math.Clamp(
        Position + (Playing ? Math.Max(0, (now - UpdatedAt).TotalSeconds) : 0), Minimum, Maximum);
}

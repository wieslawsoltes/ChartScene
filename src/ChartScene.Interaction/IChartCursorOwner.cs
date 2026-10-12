namespace ChartScene.Interaction;

/// <summary>
/// Optional contract for a captured modifier that supplies View.Cursor and View.Hits itself.
/// While true, automatic rollover queries do not replace its overlay or read source snapshots.
/// Other modifiers and uncaptured pointer movement retain the normal rollover behavior.
/// </summary>
public interface IChartCursorOwner
{
    bool OwnsCursor { get; }
}

namespace SpacePeek.Hud;

/// <summary>
/// A preview whose final size is only known after an async load. The HUD waits for this before
/// showing the window, so the user never sees a placeholder-sized window resize into place.
/// </summary>
internal interface IDeferredPreview
{
    System.Threading.Tasks.Task ReadyTask { get; }
}

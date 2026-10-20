namespace DeviceDeck.Core.Extensions;

// Optional interfaces a provider may implement on top of the frozen abstractions.
// They exist because the contract has a photos.indexProgress event that IPhotoLibrary cannot raise;
// proposed for Abstractions after round 1.

public sealed record PhotoIndexProgress(int Done, int Total);

/// <summary>Implemented by an <see cref="Abstractions.IPhotoLibrary"/> that reads its library in the background.</summary>
public interface IPhotoIndexProgressSource
{
    event EventHandler<PhotoIndexProgress>? IndexProgress;
}
